using UnityEngine;

namespace FourWayPong
{
    public static class PongAudio
    {
        private static AudioSource _source;
        private static AudioClip _paddle;
        private static AudioClip _wall;
        private static AudioClip _score;
        private static AudioClip _menu;

        public static void PlayPaddle() => Play(GetPaddle());
        public static void PlayWall() => Play(GetWall());
        public static void PlayScore() => Play(GetScore());
        public static void PlayMenu() => Play(GetMenu());

        private static void Play(AudioClip clip)
        {
            if (PongSettings.EffectiveVolume <= 0.001f)
                return;

            EnsureSource();
            _source.PlayOneShot(clip, PongSettings.EffectiveVolume);
        }

        private static void EnsureSource()
        {
            if (_source != null)
                return;

            GameObject obj = new GameObject("Pong Audio");
            Object.DontDestroyOnLoad(obj);
            _source = obj.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
        }

        private static AudioClip GetPaddle() => _paddle ??= MakeTone("Paddle", 620f, 0.055f, 0.48f, 0.18f);
        private static AudioClip GetWall() => _wall ??= MakeTone("Wall", 330f, 0.045f, 0.30f, 0.08f);
        private static AudioClip GetScore() => _score ??= MakeTone("Score", 190f, 0.20f, 0.55f, 0.55f);
        private static AudioClip GetMenu() => _menu ??= MakeTone("Menu", 760f, 0.035f, 0.22f, 0.02f);

        private static AudioClip MakeTone(string name, float frequency, float duration, float amplitude, float sweep)
        {
            const int sampleRate = 44100;
            int sampleCount = Mathf.Max(64, Mathf.RoundToInt(sampleRate * duration));
            float[] data = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                float p = (float)i / sampleCount;
                float env = Mathf.Sin(Mathf.PI * Mathf.Clamp01(p));
                float f = frequency * Mathf.Lerp(1f, 1f + sweep, p);
                data[i] = Mathf.Sin(2f * Mathf.PI * f * t) * amplitude * env;
            }

            AudioClip clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
