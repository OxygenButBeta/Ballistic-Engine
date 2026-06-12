using System;
using System.Runtime.InteropServices;
using OpenTK.Audio.OpenAL;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenALAudio;

// OpenAL implementation of IAudioBackend — the ONLY file in the engine allowed to reference
// OpenTK.Audio.OpenAL, mirroring how Physics/Bepu is the only place that touches BepuPhysics.
// Injected at bootstrap into the static `Audio` facade; the Engine layer only ever sees
// IAudioBackend / IAudioVoice.
//
// Design: a fixed pool of OpenAL sources ("voices") is generated up front (hardware caps the
// count anyway, ~256 on most drivers). Play() grabs a free voice, binds the requested buffer,
// configures it, and starts it. Update() recycles voices that finished (one-shots) back to the
// pool. Buffers are created on demand from decoded AudioData and cached by the engine's AudioClip.
public sealed class OpenALBackend : IAudioBackend {
    const int VoicePoolSize = 64;

    ALDevice device;
    ALContext context;
    bool initialized;

    readonly OpenALVoice[] pool = new OpenALVoice[VoicePoolSize];
    float masterVolume = 1f;

    // ---- Live "follow the Windows default output device" (ALC_SOFT_reopen_device) -------------
    // OpenAL binds to whatever was default WHEN the device opened; it does not auto-follow the OS
    // default the way a WASAPI shared-mode app does. We poll the current default name each frame and,
    // when it changes, reopen the same ALCdevice onto the new default — every voice/buffer/context
    // survives, so playback reroutes live with no restart. Requires OpenAL-Soft 1.21+; on older libs
    // the extension is absent and we no-op (audio just stays on the startup device, as before).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool ReopenDeviceSoftDelegate(ALDevice device, string deviceName, int[] attribs);
    ReopenDeviceSoftDelegate reopenDevice;   // null when the extension is unavailable
    string currentDefaultName;               // last-seen OS default; change triggers a reopen
    int deviceCheckCountdown;                // throttle: only poll the default every N frames

    public OpenALBackend() {
        try {
            device = ALC.OpenDevice(null);   // null = system default output device
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

            // Linear-clamped rolloff matches Unity's default AudioSource curve closely enough
            // and respects MinDistance/MaxDistance directly (ReferenceDistance/MaxDistance).
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
            // OpenAL native lib missing (headless CI, no soundcard) must never crash the engine.
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

    // Binds alcReopenDeviceSOFT (if the running OpenAL-Soft exposes it) and records the current OS
    // default device name as the baseline to diff against. No-op on libs without the extension.
    void BindDefaultDeviceFollow() {
        currentDefaultName = CurrentDefaultDeviceName();
        // The extension is per-device; checking ALDevice.Null queries the global list/extension set.
        if (!ALC.IsExtensionPresent(device, "ALC_SOFT_reopen_device"))
            return;
        IntPtr fn = ALC.GetProcAddress(device, "alcReopenDeviceSOFT");
        if (fn != IntPtr.Zero)
            reopenDevice = Marshal.GetDelegateForFunctionPointer<ReopenDeviceSoftDelegate>(fn);
    }

    // The name OpenAL reports for the CURRENT system default output. Empty string if unknown — we
    // treat "" specially (never reopen onto an empty name). DefaultAllDevicesSpecifier tracks the OS
    // default and updates live as the user changes it in Windows sound settings.
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
        // Span overload uploads the whole array; OpenAL infers the byte count from the span length.
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
        // Stop any voice still bound to this buffer so the driver lets us delete it.
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
            return SilentVoice.Instance;   // pool exhausted: silently drop (Unity behaves the same)

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
        // Orientation is 6 floats: forward (at) then up.
        System.Span<float> orientation = stackalloc float[6] {
            listener.Forward.X, listener.Forward.Y, listener.Forward.Z,
            listener.Up.X, listener.Up.Y, listener.Up.Z,
        };
        AL.Listener(ALListenerfv.Orientation, orientation.ToArray());

        // Recycle finished one-shots back into the pool.
        foreach (OpenALVoice voice in pool)
            voice.RecycleIfFinished();

        FollowDefaultDeviceIfChanged();
    }

    // Polls the OS default output (throttled) and reopens the device onto it when it changes, so
    // switching the Windows output device reroutes audio live. No-op without the extension.
    void FollowDefaultDeviceIfChanged() {
        if (reopenDevice is null)
            return;
        if (--deviceCheckCountdown > 0)   // ~ every 30 frames (twice a second) is plenty for a user action
            return;
        deviceCheckCountdown = 30;

        string now = CurrentDefaultDeviceName();
        if (now.Length == 0 || now == currentDefaultName)
            return;

        // Reopen the SAME device handle onto the new default. Passing the default name (not null) is
        // what OpenAL-Soft's docs recommend; voices, buffers, and the context all carry over.
        bool ok = reopenDevice(device, now, null);
        if (ok) {
            currentDefaultName = now;
            Debugging.Log($"Audio: output device switched to '{now}'.");
        }
        else {
            CheckError("ReopenDevice");
            // Don't spin: adopt the name anyway so we don't retry the same failing reopen every tick.
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

    // Logs and clears the OpenAL error flag; returns true if there WAS an error.
    static bool CheckError(string where) {
        ALError error = AL.GetError();
        if (error == ALError.NoError)
            return false;
        Debugging.LogWarning($"Audio (OpenAL) error in {where}: {AL.GetErrorString(error)}");
        return true;
    }
}
