using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TabMirror.Host.Capture;

/// <summary>
/// Captures desktop audio via WASAPI Loopback using raw COM P/Invoke.
/// Zero dependencies. Converts IEEE_FLOAT (32-bit) to PCM 16-bit for Android.
/// </summary>
public sealed unsafe class WasapiLoopback : IDisposable
{
    // ── COM GUIDs & Enums ────────────────────────────────────────────────
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator  = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioClient         = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient  = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void CoCreateInstance(
        in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        in Guid riid, out IntPtr ppv);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_PCM        = new("00000001-0000-0010-8000-00aa00389b71");

    // ── VTable Slots (IUnknown=3) ────────────────────────────────────────
    // IMMDeviceEnumerator
    private const int Enum_GetDefaultAudioEndpoint = 4;
    // IMMDevice
    private const int Dev_Activate = 3;
    // IAudioClient
    private const int AC_Initialize = 3;
    private const int AC_GetBufferSize = 4;
    private const int AC_GetStreamLatency = 5;
    private const int AC_GetCurrentPadding = 6;
    private const int AC_GetMixFormat = 8;
    private const int AC_Start = 10;
    private const int AC_Stop = 11;
    private const int AC_GetService = 14;
    // IAudioCaptureClient
    private const int Cap_GetBuffer = 3;
    private const int Cap_ReleaseBuffer = 4;
    private const int Cap_GetNextPacketSize = 5;

    // ── State ────────────────────────────────────────────────────────────
    private IntPtr _enumerator;
    private IntPtr _device;
    private IntPtr _audioClient;
    private IntPtr _captureClient;
    
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    public delegate void AudioDataEvent(byte[] pcm16Data);
    public event AudioDataEvent? OnAudioData;

    private Thread? _captureThread;
    private bool _running;
    private bool _isFloat;
    private int _bitsPerSample;

    public WasapiLoopback()
    {
        CoCreateInstance(in CLSID_MMDeviceEnumerator, IntPtr.Zero, 1, in IID_IMMDeviceEnumerator, out _enumerator);

        // Get Default Render Device (eRender=0, eConsole=0)
        delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int> getDefault = 
            (delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)GetVTable(_enumerator)[Enum_GetDefaultAudioEndpoint];
        
        IntPtr dev;
        Check(getDefault(_enumerator, 0, 0, &dev), "GetDefaultAudioEndpoint");
        _device = dev;

        // Activate IAudioClient
        var iidAc = IID_IAudioClient;
        IntPtr ac;
        delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int> activate = 
            (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)GetVTable(_device)[Dev_Activate];
        Check(activate(_device, &iidAc, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, &ac), "Activate IAudioClient");
        _audioClient = ac;

        // Get Mix Format
        WAVEFORMATEXTENSIBLE* mixFormat;
        delegate* unmanaged[Stdcall]<IntPtr, WAVEFORMATEXTENSIBLE**, int> getMixFormat = 
            (delegate* unmanaged[Stdcall]<IntPtr, WAVEFORMATEXTENSIBLE**, int>)GetVTable(_audioClient)[AC_GetMixFormat];
        Check(getMixFormat(_audioClient, &mixFormat), "GetMixFormat");

        SampleRate = (int)mixFormat->Format.nSamplesPerSec;
        Channels = mixFormat->Format.nChannels;
        _bitsPerSample = mixFormat->Format.wBitsPerSample;

        // WAVE_FORMAT_EXTENSIBLE = 0xFFFE = 65534
        if (mixFormat->Format.wFormatTag == 65534)
        {
            if (mixFormat->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                _isFloat = true;
            else if (mixFormat->SubFormat == KSDATAFORMAT_SUBTYPE_PCM)
                _isFloat = false;
            else
                throw new NotSupportedException($"Unsupported Extensible SubFormat: {mixFormat->SubFormat}");
        }
        else if (mixFormat->Format.wFormatTag == 1) // WAVE_FORMAT_PCM
        {
            _isFloat = false;
        }
        else if (mixFormat->Format.wFormatTag == 3) // WAVE_FORMAT_IEEE_FLOAT
        {
            _isFloat = true;
        }
        else
        {
            throw new NotSupportedException($"Unsupported FormatTag: {mixFormat->Format.wFormatTag}");
        }

        // Initialize (AUDCLNT_SHAREMODE_SHARED = 0, AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000)
        delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, WAVEFORMATEXTENSIBLE*, Guid*, int> initialize = 
            (delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, WAVEFORMATEXTENSIBLE*, Guid*, int>)GetVTable(_audioClient)[AC_Initialize];
        
        Check(initialize(_audioClient, 0, 0x00020000, 0, 0, mixFormat, null), "Initialize IAudioClient");
        Marshal.FreeCoTaskMem((IntPtr)mixFormat);

        // Get IAudioCaptureClient
        var iidCap = IID_IAudioCaptureClient;
        IntPtr cap;
        delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int> getService = 
            (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)GetVTable(_audioClient)[AC_GetService];
        Check(getService(_audioClient, &iidCap, &cap), "GetService IAudioCaptureClient");
        _captureClient = cap;
    }

    public void Start()
    {
        if (_running) return;

        delegate* unmanaged[Stdcall]<IntPtr, int> start = 
            (delegate* unmanaged[Stdcall]<IntPtr, int>)GetVTable(_audioClient)[AC_Start];
        Check(start(_audioClient), "Start AudioClient");

        _running = true;
        _captureThread = new Thread(CaptureLoop) { IsBackground = true };
        _captureThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _captureThread?.Join();
    }

    private void CaptureLoop()
    {
        delegate* unmanaged[Stdcall]<IntPtr, uint*, int> getNextPacketSize = 
            (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)GetVTable(_captureClient)[Cap_GetNextPacketSize];
        delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, ulong*, ulong*, int> getBuffer = 
            (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, ulong*, ulong*, int>)GetVTable(_captureClient)[Cap_GetBuffer];
        delegate* unmanaged[Stdcall]<IntPtr, uint, int> releaseBuffer = 
            (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)GetVTable(_captureClient)[Cap_ReleaseBuffer];

        int bytesPerSample = _bitsPerSample / 8;
        int blockAlign = Channels * bytesPerSample;

        while (_running)
        {
            Thread.Sleep(10); // Check ~100x a second

            uint packetLength = 0;
            getNextPacketSize(_captureClient, &packetLength);

            while (packetLength > 0)
            {
                byte* data;
                uint numFrames, flags;
                ulong pos, ts;

                int hr = getBuffer(_captureClient, &data, &numFrames, &flags, &pos, &ts);
                if (hr < 0) break;

                if (numFrames > 0 && OnAudioData != null)
                {
                    int totalSamples = (int)numFrames * Channels;
                    byte[] pcm16 = new byte[totalSamples * 2];

                    // 0x2 = AUDCLNT_BUFFERFLAGS_SILENT
                    if ((flags & 0x2) == 0)
                    {
                        if (_isFloat)
                        {
                            float* floatData = (float*)data;
                            for (int i = 0; i < totalSamples; i++)
                            {
                                float sample = floatData[i];
                                if (sample > 1.0f) sample = 1.0f;
                                if (sample < -1.0f) sample = -1.0f;
                                short val = (short)(sample * 32767f);
                                pcm16[i * 2] = (byte)(val & 0xFF);
                                pcm16[i * 2 + 1] = (byte)((val >> 8) & 0xFF);
                            }
                        }
                        else if (_bitsPerSample == 16)
                        {
                            // Already 16-bit PCM, just copy
                            Marshal.Copy((IntPtr)data, pcm16, 0, pcm16.Length);
                        }
                        else if (_bitsPerSample == 24)
                        {
                            // 24-bit PCM to 16-bit
                            for (int i = 0; i < totalSamples; i++)
                            {
                                // 24-bit is 3 bytes, little endian. We take the top 2.
                                pcm16[i * 2]     = data[i * 3 + 1];
                                pcm16[i * 2 + 1] = data[i * 3 + 2];
                            }
                        }
                        else if (_bitsPerSample == 32)
                        {
                            // 32-bit INT PCM to 16-bit
                            for (int i = 0; i < totalSamples; i++)
                            {
                                pcm16[i * 2]     = data[i * 4 + 2];
                                pcm16[i * 2 + 1] = data[i * 4 + 3];
                            }
                        }
                    }

                    OnAudioData(pcm16);
                }

                releaseBuffer(_captureClient, numFrames);
                getNextPacketSize(_captureClient, &packetLength);
            }
        }

        delegate* unmanaged[Stdcall]<IntPtr, int> stop = 
            (delegate* unmanaged[Stdcall]<IntPtr, int>)GetVTable(_audioClient)[AC_Stop];
        stop(_audioClient);
    }

    private static IntPtr* GetVTable(IntPtr ptr) => *(IntPtr**)ptr;
    
    private static void Check(int hr, string name)
    {
        if (hr < 0) throw new COMException($"[WasapiLoopback] {name} failed: 0x{hr:X8}");
    }

    public void Dispose()
    {
        Stop();
        if (_captureClient != IntPtr.Zero) Marshal.Release(_captureClient);
        if (_audioClient != IntPtr.Zero) Marshal.Release(_audioClient);
        if (_device != IntPtr.Zero) Marshal.Release(_device);
        if (_enumerator != IntPtr.Zero) Marshal.Release(_enumerator);
    }
}
