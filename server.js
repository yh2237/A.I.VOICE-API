'use strict';

const express = require('express');
const fs = require('fs');
const path = require('path');
const yaml = require('js-yaml');
const { AiVoiceBridge } = require('./lib/aivoice');

const config = yaml.load(fs.readFileSync(path.join(__dirname, 'config', 'config.yml'), 'utf8'));

const DLL_PATH = process.env.AIVOICE_DLL_PATH || config.aivoice?.dll_path || 'C:\\Program Files\\AI\\AIVoice\\AIVoiceEditor\\AI.Talk.Editor.Api.dll';
const HOST_NAME = process.env.AIVOICE_HOST_NAME || config.aivoice?.host_name || '';
const PORT = parseInt(process.env.PORT || config.server?.port || '58080', 10);
const PS1_PATH = path.join(__dirname, 'scripts', 'aivoice_synth.ps1');

const app = express();
app.use(express.json());

const bridge = new AiVoiceBridge(PS1_PATH, DLL_PATH, HOST_NAME);

app.get('/health', (_req, res) => {
    res.json({ status: 'ok' });
});

app.get('/api/status', (_req, res) => {
    res.json({
        connected: bridge.ready,
        hostName: bridge.currentHostName,
        version: bridge.version,
        presetNames: bridge.presetNames,
    });
});

app.get('/api/presets', (_req, res) => {
    if (!bridge.ready) {
        return res.status(503).json({ error: 'A.I.VOICE not connected' });
    }
    res.json({ presets: bridge.presetNames });
});

app.post('/api/reconnect', async (_req, res) => {
    try {
        await bridge.connect();
        res.json({
            connected: true,
            hostName: bridge.currentHostName,
            presetNames: bridge.presetNames,
        });
    } catch (err) {
        res.status(503).json({ error: err.message });
    }
});

app.post('/api/synthesize', async (req, res) => {
    if (!bridge.ready) {
        return res.status(503).json({ error: 'A.I.VOICE not connected' });
    }

    const {
        text,
        preset,
        speed,
        pitch,
        pitchRange,
        volume,
        middlePause,
        longPause,
        sentencePause,
    } = req.body;

    if (!text || typeof text !== 'string') {
        return res.status(400).json({ error: 'Missing or invalid "text" field' });
    }

    try {
        const wavBuffer = await bridge.synthesize({
            text,
            preset,
            speed: speed ?? 1.0,
            pitch: pitch ?? 1.0,
            pitchRange: pitchRange ?? 1.0,
            volume: volume ?? 1.0,
            middlePause: middlePause ?? 150,
            longPause: longPause ?? 370,
            sentencePause: sentencePause ?? 800,
        });

        res.set({
            'Content-Type': 'audio/wav',
            'Content-Length': wavBuffer.length.toString(),
        });
        res.send(wavBuffer);
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

async function start() {
    try {
        await bridge.connect();
        console.log(`A.I.VOICE connected. Host: ${bridge.currentHostName}`);
        console.log(`Presets: ${bridge.presetNames.join(', ')}`);
    } catch (err) {
        console.error(`Failed to connect to A.I.VOICE: ${err.message}`);
        console.log('Server will start without A.I.VOICE. Use POST /api/reconnect to retry.');
    }

    const server = app.listen(PORT, () => {
        console.log(`A.I.VOICE API server listening on port ${PORT}`);
    });

    process.on('SIGINT', async () => {
        console.log('\nShutting down...');
        server.close();
        await bridge.shutdown();
        process.exit(0);
    });

    process.on('SIGTERM', async () => {
        console.log('\nShutting down...');
        server.close();
        await bridge.shutdown();
        process.exit(0);
    });
}

start().catch(err => {
    console.error('Failed to start server:', err);
    process.exit(1);
});
