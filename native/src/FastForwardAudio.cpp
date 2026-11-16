#include "FastForwardAudio.h"

#include <algorithm>
#include <cmath>

namespace ndscore
{

namespace
{

// Grains of 50 ms joined by 10 ms crossfades; each join may move up to 8 ms to line the
// waveforms up. Shorter grains sound buzzy, longer ones make the jumps more noticeable.
constexpr double kGrainSeconds = 0.050;
constexpr double kOverlapSeconds = 0.010;
constexpr double kSearchSeconds = 0.008;
// Input kept beyond what the next grain needs, to absorb uneven emulation speed. Older
// audio is dropped so the sound never lags the picture.
constexpr double kBacklogSeconds = 0.12;
// Assumed rate of an unlimited fast-forward; the backlog limit corrects the rest.
constexpr float kUnlimitedRatio = 8.0f;

constexpr float kPi = 3.14159265358979f;

int16_t ToS16(float v)
{
    return static_cast<int16_t>(std::clamp(std::lround(v), -32768L, 32767L));
}

}

FastForwardAudio::FastForwardAudio(int sampleRate)
{
    SetSampleRate(sampleRate);
}

void FastForwardAudio::SetSampleRate(int sampleRate)
{
    std::lock_guard guard(Lock);
    Rate = std::max(8000, sampleRate);
    GrainLen = static_cast<int>(Rate * kGrainSeconds);
    Overlap = static_cast<int>(Rate * kOverlapSeconds);
    Search = static_cast<int>(Rate * kSearchSeconds);
    Fade.resize(Overlap);
    for (int i = 0; i < Overlap; i++)
        Fade[i] = 0.5f - 0.5f * std::cos(kPi * (i + 0.5f) / Overlap);
    Tail.assign(2 * Overlap, 0.0f);
    In.clear();
    Start = 0;
    HaveTail = false;
    NextNominal = 0;
    Ready.clear();
    ReadyPos = 0;
    Pos = 0.0;
    Primed = false;
}

void FastForwardAudio::Reset()
{
    std::lock_guard guard(Lock);
    In.clear();
    Start = 0;
    HaveTail = false;
    NextNominal = 0;
    Ready.clear();
    ReadyPos = 0;
    Pos = 0.0;
    Primed = false;
}

void FastForwardAudio::Push(const int16_t* frames, int count)
{
    if (count <= 0) return;
    std::lock_guard guard(Lock);
    In.reserve(In.size() + 2 * count);
    for (int i = 0; i < 2 * count; i++)
        In.push_back(frames[i]);
    // Nobody is listening (no audio device, or playback stalled): keep at most a second.
    if (Frames() > Rate) Consume(Frames() - Rate);
}

void FastForwardAudio::Consume(int frames)
{
    frames = std::clamp(frames, 0, Frames());
    Start += frames;
    if (Start > 4096 && Start * 2 > static_cast<int>(In.size() / 2))
    {
        In.erase(In.begin(), In.begin() + 2 * Start);
        Start = 0;
    }
}

void FastForwardAudio::LimitBacklog(int keep)
{
    const int excess = Frames() - keep;
    if (excess <= 0) return;
    Consume(excess);
    NextNominal = std::max(0, NextNominal - excess);
    Pos = std::max(0.0, Pos - excess);
}

int FastForwardAudio::Pull(int16_t* out, int count, float speed, FastForwardSound mode)
{
    if (mode == FastForwardSound::Mute || count <= 0) return 0;
    const float ratio = speed > 0.0f ? std::max(1.0f, speed) : kUnlimitedRatio;
    std::lock_guard guard(Lock);
    return mode == FastForwardSound::SpedUp ? PullSpedUp(out, count, ratio) : PullGrains(out, count, ratio);
}

// --- Normal pitch (WSOLA) ---------------------------------------------------------------

bool FastForwardAudio::MakeGrain(float ratio)
{
    const int avail = Frames();
    if (avail < GrainLen) return false;

    // Where the next grain may start: around its nominal position, within the input we have.
    int lo = HaveTail ? std::max(0, NextNominal - Search) : 0;
    int hi = HaveTail ? NextNominal + Search : 0;
    hi = std::min(hi, avail - GrainLen);
    lo = std::min(lo, hi);

    int best = lo;
    if (HaveTail && hi > lo)
    {
        // Pick the start whose opening best continues the previous grain's ending
        // (normalised cross-correlation of the mono mix over the crossfade).
        float tailMono[2048];
        const int n = std::min(Overlap, 2048);
        for (int i = 0; i < n; i++) tailMono[i] = Tail[2 * i] + Tail[2 * i + 1];

        float bestScore = -1e30f;
        for (int k = lo; k <= hi; k++)
        {
            const float* p = At(k);
            float dot = 0.0f, energy = 0.0f;
            for (int i = 0; i < n; i += 2)
            {
                const float y = p[2 * i] + p[2 * i + 1];
                dot += tailMono[i] * y;
                energy += y * y;
            }
            const float score = dot / std::sqrt(energy + 1.0f);
            if (score > bestScore)
            {
                bestScore = score;
                best = k;
            }
        }
    }

    const float* g = At(best);
    const int emit = GrainLen - Overlap;
    Ready.resize(2 * emit);
    ReadyPos = 0;
    for (int i = 0; i < Overlap; i++)
    {
        const float in = Fade[i], out = 1.0f - Fade[i];
        const float prevL = HaveTail ? Tail[2 * i] : 0.0f;
        const float prevR = HaveTail ? Tail[2 * i + 1] : 0.0f;
        Ready[2 * i] = prevL * out + g[2 * i] * in;
        Ready[2 * i + 1] = prevR * out + g[2 * i + 1] * in;
    }
    std::copy(g + 2 * Overlap, g + 2 * emit, Ready.begin() + 2 * Overlap);
    std::copy(g + 2 * emit, g + 2 * GrainLen, Tail.begin());
    HaveTail = true;

    // The stream moves on `ratio` times what was played; everything before the next
    // search window is never needed again.
    NextNominal = best + std::max(1, static_cast<int>(std::lround(ratio * emit)));
    const int drop = std::min(std::max(0, NextNominal - Search), Frames());
    Consume(drop);
    NextNominal -= drop;
    return true;
}

int FastForwardAudio::PullGrains(int16_t* out, int count, float ratio)
{
    const int emit = GrainLen - Overlap;
    LimitBacklog(NextNominal + Search + GrainLen + static_cast<int>(ratio * emit) + static_cast<int>(Rate * kBacklogSeconds));

    int written = 0;
    while (written < count)
    {
        const int ready = static_cast<int>(Ready.size() / 2) - ReadyPos;
        if (ready > 0)
        {
            const int n = std::min(ready, count - written);
            const float* src = Ready.data() + 2 * ReadyPos;
            for (int i = 0; i < 2 * n; i++) out[2 * written + i] = ToS16(src[i]);
            ReadyPos += n;
            written += n;
            continue;
        }
        if (MakeGrain(ratio)) continue;
        if (!HaveTail) break;

        // The emulator fell behind: let the last grain fade out instead of cutting it.
        Ready.resize(2 * Overlap);
        ReadyPos = 0;
        for (int i = 0; i < Overlap; i++)
        {
            const float out = 1.0f - Fade[i];
            Ready[2 * i] = Tail[2 * i] * out;
            Ready[2 * i + 1] = Tail[2 * i + 1] * out;
        }
        HaveTail = false;
        NextNominal = 0;
    }
    return written;
}

// --- Sped up ---------------------------------------------------------------------------

int FastForwardAudio::PullSpedUp(int16_t* out, int count, float ratio)
{
    LimitBacklog(static_cast<int>(Pos + ratio * count) + static_cast<int>(Rate * kBacklogSeconds));

    const int avail = Frames();
    if (!Primed)
    {
        if (avail < Pos + ratio * count + Rate / 30) return 0;   // two video frames' worth
        Primed = true;
    }

    int written = 0;
    while (written < count && Pos + ratio <= avail)
    {
        // Average the frames this output frame covers: a cheap low-pass against aliasing.
        const int a = static_cast<int>(Pos);
        const int b = std::max(a + 1, std::min(avail, static_cast<int>(Pos + ratio)));
        float l = 0.0f, r = 0.0f;
        for (int i = a; i < b; i++)
        {
            l += At(i)[0];
            r += At(i)[1];
        }
        out[2 * written] = ToS16(l / (b - a));
        out[2 * written + 1] = ToS16(r / (b - a));
        written++;
        Pos += ratio;
    }
    const int whole = static_cast<int>(Pos);
    Consume(whole);
    Pos -= whole;
    if (written < count) Primed = false;   // ran dry: build the cushion up again
    return written;
}

}
