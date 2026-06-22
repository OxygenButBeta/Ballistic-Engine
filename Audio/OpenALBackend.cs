using System.Runtime.InteropServices;
using OpenTK.Audio.OpenAL;

namespace BallisticEngine.OpenALAudio;

public sealed class OpenALBackend : IAudioBackend {
    const int VoicePoolSize = 64;

    ALDevice device;
    ALContext context;
    bool initialized;

    readonly OpenALVoice[] pool = new OpenALVoice[VoicePoolSize];
    float masterVolume = 1f;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool ReopenDeviceSoftDelegate(ALDevice device, string deviceName, int[] attribs);
    ReopenDeviceSoftDelegate reopenDevice;
    string currentDefaultName;
    int deviceCheckCountdown;

    public OpenALBackend() {
        try {
            device = ALC.OpenDevice(null);
            if (device == ALDevice.Null) {
                Debugging.LogWarning("Audio: no OpenAL output device; sound is disabled this session.");
                return;
            }
            context = ALC.CreateContext(device, (int[])null);
            if (context == ALContext.Null || !ALC.MakeContextCurrent(context)) {
                Debugging.LogWarning("Audio: failed to create an OpenAL context; sound is disabled.");
                Teardown();
                return;
            }

            AL.DistanceModel(ALDistanceModel.LinearDistanceClamped);

            for (int i = 0; i < pool.Length; i++)
                pool[i] = new OpenALVoice(AL.GenSource());

            initialized = true;
            CheckError("init");
            BindDefaultDeviceFollow();
            Debugging.Log($"Audio: OpenAL initialized ({VoicePoolSize} voices"
                + (reopenDevice is not null ? ", follows OS default device" : "") + ").");
        }
        catch (System.Exception e) {
            Debugging.LogWarning($"Audio: OpenAL unavailable ({e.Message}); sound is disabled this session.");
            Teardown();
        }
    }

    public bool IsAvailable => initialized;

    public float MasterVolume {
        get => masterVolume;
        set {
            masterVolume = MathHelper.Clamp(value, 0f, 1f);
            if (initialized)
                AL.Listener(ALListenerf.Gain, masterVolume);
        }
    }

    void BindDefaultDeviceFollow() {
        currentDefaultName = CurrentDefaultDeviceName();
        if (!ALC.IsExtensionPresent(device, "ALC_SOFT_reopen_device"))
            return;
        IntPtr fn = ALC.GetProcAddress(device, "alcReopenDeviceSOFT");
        if (fn != IntPtr.Zero)
            reopenDevice = Marshal.GetDelegateForFunctionPointer<ReopenDeviceSoftDelegate>(fn);
    }

    string CurrentDefaultDeviceName() {
        try {
            return ALC.GetString(ALDevice.Null, AlcGetString.DefaultAllDevicesSpecifier) ?? "";
        }
        catch {
            return "";
        }
    }

    public int CreateBuffer(in AudioData data) {
        if (!initialized || !data.IsValid)
            return 0;

        int buffer = AL.GenBuffer();
        ALFormat format = data.Channels >= 2 ? ALFormat.Stereo16 : ALFormat.Mono16;
        AL.BufferData<short>(buffer, format, data.Samples, data.SampleRate);
        if (CheckError("CreateBuffer")) {
            AL.DeleteBuffer(buffer);
            return 0;
        }
        return buffer;
    }

    public void DestroyBuffer(int bufferHandle) {
        if (!initialized || bufferHandle == 0)
            return;
        foreach (OpenALVoice voice in pool) {
            if (voice.BoundBuffer == bufferHandle && voice.IsPlaying)
                voice.Stop();
        }
        AL.DeleteBuffer(bufferHandle);
        CheckError("DestroyBuffer");
    }

    public IAudioVoice Play(int bufferHandle, in AudioVoiceParams p) {
        if (!initialized || bufferHandle == 0)
            return SilentVoice.Instance;

        OpenALVoice voice = AcquireVoice();
        if (voice is null)
            return SilentVoice.Instance;

        voice.Configure(bufferHandle, in p);
        voice.Play();
        CheckError("Play");
        return voice;
    }

    OpenALVoice AcquireVoice() {
        foreach (OpenALVoice voice in pool) {
            if (!voice.IsPlaying && !voice.Reserved)
                return voice;
        }
        return null;
    }

    public void Update(in AudioListenerState listener) {
        if (!initialized)
            return;

        AL.Listener(ALListener3f.Position, listener.Position.X, listener.Position.Y, listener.Position.Z);
        AL.Listener(ALListener3f.Velocity, listener.Velocity.X, listener.Velocity.Y, listener.Velocity.Z);
        System.Span<float> orientation = stackalloc float[6] {
            listener.Forward.X, listener.Forward.Y, listener.Forward.Z,
            listener.Up.X, listener.Up.Y, listener.Up.Z,
        };
        AL.Listener(ALListenerfv.Orientation, orientation.ToArray());

        foreach (OpenALVoice voice in pool)
            voice.RecycleIfFinished();

        FollowDefaultDeviceIfChanged();
    }

    void FollowDefaultDeviceIfChanged() {
        if (reopenDevice is null)
            return;
        if (--deviceCheckCountdown > 0) return;
        deviceCheckCountdown = 30;

        string now = CurrentDefaultDeviceName();
        if (now.Length == 0 || now == currentDefaultName)
            return;

        bool ok = reopenDevice(device, now, null);
        if (ok) {
            currentDefaultName = now;
            Debugging.Log($"Audio: output device switched to '{now}'.");
        }
        else {
            CheckError("ReopenDevice");
            currentDefaultName = now;
        }
    }

    public void Dispose() => Teardown();

    void Teardown() {
        if (pool != null) {
            foreach (OpenALVoice voice in pool) {
                if (voice != null) {
                    voice.Stop();
                    AL.DeleteSource(voice.Source);
                }
            }
        }
        if (context != ALContext.Null) {
            ALC.MakeContextCurrent(ALContext.Null);
            ALC.DestroyContext(context);
            context = ALContext.Null;
        }
        if (device != ALDevice.Null) {
            ALC.CloseDevice(device);
            device = ALDevice.Null;
        }
        initialized = false;
    }

    static bool CheckError(string where) {
        ALError error = AL.GetError();
        if (error == ALError.NoError)
            return false;
        Debugging.LogWarning($"Audio (OpenAL) error in {where}: {AL.GetErrorString(error)}");
        return true;
    }
}
