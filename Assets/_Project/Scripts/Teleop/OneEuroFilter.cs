using UnityEngine;

namespace GemStore.Teleop
{
    /// <summary>
    /// One Euro filter for a 3D position (Casiez et al.): strong smoothing while the hand is nearly still (removes jitter), little lag
    /// while it moves fast. <c>minCutoff</c> in Hz, <c>beta</c> the speed coefficient, <c>derivativeCutoff</c> in Hz.
    /// </summary>
    public sealed class OneEuroFilter
    {
        private readonly float minCutoff;
        private readonly float beta;
        private readonly float derivativeCutoff;
        private Vector3 previous;
        private Vector3 previousDerivative;
        private float previousTime;
        private bool initialized;

        public OneEuroFilter(float minCutoff, float beta, float derivativeCutoff)
        {
            this.minCutoff = Mathf.Max(0.01f, minCutoff);
            this.beta = Mathf.Max(0f, beta);
            this.derivativeCutoff = Mathf.Max(0.01f, derivativeCutoff);
        }

        public void Reset()
        {
            initialized = false;
        }

        public Vector3 Filter(Vector3 value, float time)
        {
            if (!initialized)
            {
                initialized = true;
                previous = value;
                previousDerivative = Vector3.zero;
                previousTime = time;
                return value;
            }

            float dt = Mathf.Max(time - previousTime, 1e-4f);
            previousTime = time;

            Vector3 derivative = (value - previous) / dt;
            previousDerivative = Vector3.Lerp(previousDerivative, derivative, Alpha(derivativeCutoff, dt));

            float cutoff = minCutoff + beta * previousDerivative.magnitude;
            previous = Vector3.Lerp(previous, value, Alpha(cutoff, dt));
            return previous;
        }

        private static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }
}
