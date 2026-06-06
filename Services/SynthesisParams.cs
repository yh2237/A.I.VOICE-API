namespace AIVoiceApi.Services;

public class SynthesisParams
{
    public string? Text { get; set; }
    public string? Preset { get; set; }
    public double Speed { get; set; } = 1.0;
    public double Pitch { get; set; } = 1.0;
    public double PitchRange { get; set; } = 1.0;
    public double Volume { get; set; } = 1.0;
    public int MiddlePause { get; set; } = 150;
    public int LongPause { get; set; } = 370;
    public int SentencePause { get; set; } = 800;
    public int Priority { get; set; } = 0;
}
