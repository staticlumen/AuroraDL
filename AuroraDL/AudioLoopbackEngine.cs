using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace auroradl;

internal sealed class AudioLoopbackEngine : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public bool IsRunning => _capture is not null;

    public event Action<float[]>? SamplesAvailable;
    public event Action<string>? Error;

    public void Start()
    {
        if (_capture is not null) return;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _capture = new WasapiLoopbackCapture(device);
            SampleRate = _capture.WaveFormat.SampleRate;
            Channels = _capture.WaveFormat.Channels;
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Failed to start loopback: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        if (_capture is null) return;
        try
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.StopRecording();
            _capture.Dispose();
        }
        finally
        {
            _capture = null;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Error?.Invoke($"Capture stopped: {e.Exception.Message}");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture is null) return;
        try
        {
            var wf = _capture.WaveFormat;
            var samples = ConvertToMonoFloat(e.Buffer, e.BytesRecorded, wf);
            if (samples.Length > 0) SamplesAvailable?.Invoke(samples);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Audio decode error: {ex.Message}");
        }
    }

    private static float[] ConvertToMonoFloat(byte[] buffer, int bytesRecorded, WaveFormat wf)
    {
        var ch = Math.Max(1, wf.Channels);
        if (wf.Encoding == WaveFormatEncoding.IeeeFloat && wf.BitsPerSample == 32)
        {
            int total = bytesRecorded / 4;
            int frames = total / ch;
            var mono = new float[frames];
            int idx = 0;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < ch; c++)
                {
                    sum += BitConverter.ToSingle(buffer, idx);
                    idx += 4;
                }
                mono[i] = sum / ch;
            }
            return mono;
        }

        if (wf.BitsPerSample == 16)
        {
            int total = bytesRecorded / 2;
            int frames = total / ch;
            var mono = new float[frames];
            int idx = 0;
            const float scale = 1.0f / short.MaxValue;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < ch; c++)
                {
                    short s = BitConverter.ToInt16(buffer, idx);
                    idx += 2;
                    sum += s * scale;
                }
                mono[i] = sum / ch;
            }
            return mono;
        }

        return Array.Empty<float>();
    }

    public void Dispose()
    {
        Stop();
    }
}

