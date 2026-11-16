#pragma once

// Sound while the game runs faster than real time. The emulator then produces several
// seconds of audio per second; played as is it would be pitched up ("chipmunk"), so by
// default it is time-compressed instead: short grains of the stream are played at their
// original pitch and the audio between them is skipped, joined by crossfades placed where
// the waveforms line up best (WSOLA). Music jumps ahead, voices and effects sound normal.
//
// Shared by the Windows and Android cores; no platform dependencies.

#include <cstdint>
#include <mutex>
#include <vector>

namespace ndscore
{

enum class FastForwardSound : int32_t
{
    NormalPitch = 0,   // grains at the original pitch (default)
    SpedUp = 1,        // everything, pitched up
    Mute = 2,
};

class FastForwardAudio
{
public:
    explicit FastForwardAudio(int sampleRate = 48000);

    /// Emulation thread: newly produced stereo frames (interleaved s16).
    void Push(const int16_t* frames, int count);

    /// Audio thread: fills `out` with up to `count` stereo frames for real-time playback
    /// of a stream running `speed` times faster (0 = unlimited). Returns frames written;
    /// the rest is left for the caller to fill with silence.
    int Pull(int16_t* out, int count, float speed, FastForwardSound mode);

    /// Forget everything buffered (fast-forward ended, game paused or unloaded).
    void Reset();

    void SetSampleRate(int sampleRate);

private:
    int Frames() const { return static_cast<int>(In.size() / 2) - Start; }
    const float* At(int frame) const { return In.data() + 2 * (Start + frame); }
    void Consume(int frames);
    void LimitBacklog(int keep);
    bool MakeGrain(float ratio);
    int PullGrains(int16_t* out, int count, float ratio);
    int PullSpedUp(int16_t* out, int count, float ratio);

    std::mutex Lock;
    int Rate;
    int GrainLen, Overlap, Search;   // frames

    std::vector<float> In;   // interleaved stereo, frames before Start already consumed
    int Start = 0;

    // Normal pitch: the last Overlap frames of the previous grain, crossfaded into the
    // next one, and the next grain's nominal start relative to the input.
    std::vector<float> Tail;
    bool HaveTail = false;
    int NextNominal = 0;
    std::vector<float> Ready;   // grain frames not yet handed to the audio device
    int ReadyPos = 0;

    // Sped up: fractional read position in the input, and whether enough input is
    // buffered to play without gaps (the emulator delivers it a video frame at a time).
    double Pos = 0.0;
    bool Primed = false;

    std::vector<float> Fade;   // raised-cosine fade-in curve, Overlap long
};

}
