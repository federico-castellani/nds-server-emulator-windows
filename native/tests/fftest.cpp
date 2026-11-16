// Offline check of FastForwardAudio: feeds a tone at N times real time, the way the
// emulation thread does, pulls it like the audio device, and measures the result.
//   fftest            -> prints pitch, gaps and largest jump per mode/speed, exit 1 on failure

#include "../src/FastForwardAudio.h"

#include <cmath>
#include <cstdio>
#include <vector>

using ndscore::FastForwardAudio;
using ndscore::FastForwardSound;

namespace
{

constexpr int kRate = 48000;
constexpr double kTone = 440.0;

struct Result
{
    double pitch;    // Hz, from zero crossings
    int silent;      // frames the module could not fill after warm-up
    int maxJump;     // largest sample-to-sample step
    double level;    // RMS relative to the input's
};

Result Run(FastForwardSound mode, float speed, double seconds)
{
    FastForwardAudio ff(kRate);
    const int callback = 512;
    const double inPerOut = speed > 0 ? speed : 8.0;
    double phase = 0.0, owed = 0.0;
    std::vector<int16_t> in, out(2 * callback), all;

    const int callbacks = static_cast<int>(seconds * kRate / callback);
    int silent = 0;
    for (int c = 0; c < callbacks; c++)
    {
        // Emulated audio produced while this callback's worth of real time passed, in
        // 800-frame "video frames" like melonDS at 48 kHz.
        owed += callback * inPerOut;
        while (owed >= 800)
        {
            in.resize(2 * 800);
            for (int i = 0; i < 800; i++)
            {
                const auto v = static_cast<int16_t>(10000 * std::sin(phase));
                in[2 * i] = in[2 * i + 1] = v;
                phase += 2 * 3.14159265358979 * kTone / kRate;
            }
            ff.Push(in.data(), 800);
            owed -= 800;
        }
        const int got = ff.Pull(out.data(), callback, speed, mode);
        if (c > 10) silent += callback - got;
        for (int i = 0; i < got; i++) all.push_back(out[2 * i]);
        for (int i = got; i < callback; i++) all.push_back(0);
    }

    Result r {};
    int crossings = 0;
    double sq = 0.0;
    const size_t from = all.size() / 4;   // skip the start-up
    for (size_t i = from + 1; i < all.size(); i++)
    {
        if ((all[i - 1] < 0) != (all[i] < 0)) crossings++;
        r.maxJump = std::max(r.maxJump, std::abs(all[i] - all[i - 1]));
        sq += double(all[i]) * all[i];
    }
    const double dur = double(all.size() - from) / kRate;
    r.pitch = crossings / 2.0 / dur;
    r.level = std::sqrt(sq / (all.size() - from)) / (10000 / std::sqrt(2.0));
    r.silent = silent;
    return r;
}

}

int main()
{
    bool ok = true;
    // A 440 Hz sine at amplitude 10000 changes by at most ~576 per sample; crossfade
    // joins may add a little, a click would be thousands.
    for (float speed : {2.0f, 3.0f, 4.0f, 8.0f, 0.0f})
    {
        const Result n = Run(FastForwardSound::NormalPitch, speed, 5.0);
        const bool good = std::fabs(n.pitch - kTone) < 15 && n.silent == 0 && n.maxJump < 1500 && n.level > 0.8;
        std::printf("normal  x%-4g pitch %6.1f Hz  silent %5d  max jump %5d  level %.2f  %s\n",
                    speed, n.pitch, n.silent, n.maxJump, n.level, good ? "ok" : "FAIL");
        ok &= good;
    }
    for (float speed : {2.0f, 3.0f})
    {
        const Result s = Run(FastForwardSound::SpedUp, speed, 5.0);
        const bool good = std::fabs(s.pitch - kTone * speed) < 30 && s.silent == 0;
        std::printf("sped up x%-4g pitch %6.1f Hz  silent %5d  max jump %5d  level %.2f  %s\n",
                    speed, s.pitch, s.silent, s.maxJump, s.level, good ? "ok" : "FAIL");
        ok &= good;
    }
    const Result m = Run(FastForwardSound::Mute, 3.0f, 1.0);
    std::printf("mute    x3    level %.2f  %s\n", m.level, m.level == 0 ? "ok" : "FAIL");
    ok &= m.level == 0;
    return ok ? 0 : 1;
}
