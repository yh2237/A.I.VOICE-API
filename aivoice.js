'use strict';

const { spawn } = require('child_process');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');

const KEEPALIVE_INTERVAL_MS = 30 * 1000;

class AiVoiceBridge {
    constructor(ps1Path, dllPath, hostName) {
        this.ps1Path = ps1Path;
        this.dllPath = dllPath;
        this.targetHostName = hostName || '';
        this.proc = null;
        this.ready = false;
        this.requestId = 0;
        this.pendingResolvers = new Map();
        this.keepAliveTimer = null;
        this.presetNames = [];
        this.currentHostName = null;
        this.version = null;
        this._synthQueue = [];
        this._synthBusy = false;
        this._connecting = false;
        this._connectPromise = null;
    }

    async connect() {
        if (this._connectPromise) return this._connectPromise;
        if (this._connecting) {
            return new Promise((resolve, reject) => {
                const check = setInterval(() => {
                    if (!this._connecting && !this._connectPromise) {
                        clearInterval(check);
                        if (this.ready) resolve(this.presetNames);
                        else reject(new Error('A.I.VOICE connection failed'));
                    }
                }, 100);
            });
        }

        this._connecting = true;
        this._connectPromise = this._doConnect();
        try {
            const result = await this._connectPromise;
            return result;
        } finally {
            this._connecting = false;
            this._connectPromise = null;
        }
    }

    async _doConnect() {
        await this._killExisting();

        return new Promise((resolve, reject) => {
            const proc = spawn('powershell.exe', [
                '-NoProfile',
                '-NonInteractive',
                '-ExecutionPolicy', 'Bypass',
                '-File', this.ps1Path,
            ], {
                stdio: ['pipe', 'pipe', 'pipe'],
                windowsHide: true,
            });

            this.proc = proc;
            this.ready = false;
            this.requestId = 0;
            this.pendingResolvers = new Map();

            let stdoutBuf = '';
            let initDone = false;
            let initResolved = false;

            proc.stdout.setEncoding('utf8');
            proc.stdout.on('data', (chunk) => {
                stdoutBuf += chunk;
                const lines = stdoutBuf.split('\n');
                stdoutBuf = lines.pop();

                for (const rawLine of lines) {
                    const line = rawLine.trim();
                    if (!line) continue;

                    if (!initDone) {
                        initDone = true;
                        let res;
                        try {
                            res = JSON.parse(line);
                        } catch {
                            this.ready = false;
                            if (!initResolved) {
                                initResolved = true;
                                reject(new Error(`A.I.VOICE: Invalid init response: ${line}`));
                            }
                            return;
                        }

                        if (res.ok) {
                            this.ready = true;
                            this.currentHostName = res.data?.hostName || this.targetHostName;
                            this.presetNames = res.data?.presetNames || [];
                            this.version = res.data?.version || null;

                            proc.stdout.removeAllListeners('data');
                            let buf2 = '';
                            proc.stdout.on('data', (c) => {
                                buf2 += c;
                                const ls = buf2.split('\n');
                                buf2 = ls.pop();
                                for (const l of ls) {
                                    const t = l.trim();
                                    if (t) this._handleResponse(t);
                                }
                            });

                            this._startKeepalive();

                            if (!initResolved) {
                                initResolved = true;
                                resolve(this.presetNames);
                            }
                        } else {
                            this.ready = false;
                            if (!initResolved) {
                                initResolved = true;
                                reject(new Error(`A.I.VOICE init failed: ${res.error}`));
                            }
                        }
                    } else {
                        this._handleResponse(line);
                    }
                }
            });

            proc.stderr.setEncoding('utf8');
            proc.stderr.on('data', (data) => {
                console.warn(`[aivoice] PS stderr: ${data.trimEnd()}`);
            });

            proc.on('close', (code) => {
                console.warn(`[aivoice] PS process exited (code=${code})`);
                this.ready = false;
                this.proc = null;

                for (const { reject: rej } of this.pendingResolvers.values()) {
                    rej(new Error('A.I.VOICE PS process unexpectedly exited'));
                }
                this.pendingResolvers.clear();

                if (!initResolved) {
                    initResolved = true;
                    reject(new Error('A.I.VOICE PS process exited before init'));
                }
            });

            proc.on('error', (err) => {
                console.error(`[aivoice] PS process error: ${err.message}`);
                if (!initResolved) {
                    initResolved = true;
                    reject(err);
                }
            });

            const initMsg = JSON.stringify({
                type: 'init',
                dllPath: this.dllPath,
                hostName: this.targetHostName,
            }) + '\n';
            proc.stdin.write(initMsg, 'utf8');
        });
    }

    async _killExisting() {
        if (this.keepAliveTimer) {
            clearInterval(this.keepAliveTimer);
            this.keepAliveTimer = null;
        }

        if (this.proc) {
            if (this.ready) {
                try {
                    await this._sendRequestRaw({ type: 'quit' });
                    await new Promise(r => setTimeout(r, 500));
                } catch (_) { }
            }

            if (!this.proc.killed) {
                try { this.proc.kill(); } catch (_) { }
            }
            this.proc = null;
            this.ready = false;
        }
    }

    _startKeepalive() {
        this.keepAliveTimer = setInterval(async () => {
            if (!this.ready || !this.proc) return;
            try {
                await this._sendRequest({ type: 'keepalive' });
            } catch (_) { }
        }, KEEPALIVE_INTERVAL_MS);
    }

    _sendRequest(msg) {
        return new Promise((resolve, reject) => {
            if (!this.proc || !this.ready) {
                return reject(new Error('A.I.VOICE not connected'));
            }
            this._sendRequestRaw(msg).then(resolve, reject);
        });
    }

    _sendRequestRaw(msg) {
        return new Promise((resolve, reject) => {
            if (!this.proc || this.proc.killed) {
                return reject(new Error('A.I.VOICE PS process not running'));
            }

            const id = ++this.requestId;
            this.pendingResolvers.set(id, { resolve, reject });
            const line = JSON.stringify(msg) + '\n';
            try {
                this.proc.stdin.write(line, 'utf8');
            } catch (err) {
                this.pendingResolvers.delete(id);
                reject(new Error(`A.I.VOICE stdin write error: ${err.message}`));
            }
        });
    }

    _handleResponse(line) {
        let res;
        try {
            res = JSON.parse(line);
        } catch {
            console.warn(`[aivoice] Invalid JSON from PS: ${line}`);
            return;
        }

        const firstEntry = this.pendingResolvers.entries().next();
        if (firstEntry.done) {
            console.warn(`[aivoice] Unsolicited response: ${line}`);
            return;
        }

        const [id, { resolve, reject }] = firstEntry.value;
        this.pendingResolvers.delete(id);

        if (res.ok) {
            resolve(res.data ?? null);
        } else {
            reject(new Error(res.error || 'A.I.VOICE unknown error'));
        }
    }

    async synthesize(params) {
        return new Promise((resolve, reject) => {
            this._synthQueue.push({ params, resolve, reject });
            this._processQueue();
        });
    }

    async _processQueue() {
        if (this._synthBusy || this._synthQueue.length === 0) return;

        this._synthBusy = true;
        const { params, resolve, reject } = this._synthQueue.shift();

        try {
            if (!this.ready) {
                reject(new Error('A.I.VOICE not connected'));
                return;
            }

            const preset = params.preset || this.presetNames[0];
            const tmpName = crypto.randomBytes(8).toString('hex');
            const tmpWavPath = path.join(os.tmpdir(), `aivoice_synth_${tmpName}.wav`);

            await this._sendRequest({
                type: 'synth',
                text: params.text,
                preset,
                speed: params.speed,
                pitch: params.pitch,
                pitchRange: params.pitchRange,
                volume: params.volume,
                middlePause: params.middlePause,
                longPause: params.longPause,
                sentencePause: params.sentencePause,
                outputPath: tmpWavPath,
            });

            const wavBuffer = await fs.promises.readFile(tmpWavPath);
            await fs.promises.unlink(tmpWavPath).catch(() => { });
            resolve(wavBuffer);
        } catch (err) {
            reject(err);
        } finally {
            this._synthBusy = false;
            if (this._synthQueue.length > 0) {
                this._processQueue();
            }
        }
    }

    async shutdown() {
        if (this.keepAliveTimer) {
            clearInterval(this.keepAliveTimer);
            this.keepAliveTimer = null;
        }

        if (this.proc && this.ready) {
            try {
                await this._sendRequestRaw({ type: 'quit' });
                await new Promise(r => setTimeout(r, 1000));
            } catch (_) { }
        }

        if (this.proc && !this.proc.killed) {
            try { this.proc.kill(); } catch (_) { }
        }
        this.proc = null;
        this.ready = false;
    }
}

module.exports = { AiVoiceBridge };
