using System;
using UnityEngine;

namespace mattatz.TeddySystem.Example {

    /// <summary>
    /// Fast Mass-Spring solver (Liu et al. 2013) — stable, damped variant.
    ///
    /// Per timestep h:
    ///   1. Damp velocities:     v  ←  v · (1 − α)
    ///   2. Inertia target:      y  =  x + h·v + h²·fext/m
    ///   3. Initialise iterate:  x* ←  y
    ///   4. For each iteration:
    ///        Local  step:  d_k  =  r_k · normalize(p_i − p_j)
    ///        Global step:  solve  (M + h²·L) x* = M·y + h²·J·d
    ///        Override forced joints in x* after solve
    ///   5. Velocity update:     v  =  (x* − x) / h
    ///   6. Zero velocity on forced/pinned joints, commit x* → x
    ///
    /// PINNED joints are embedded into the system matrix (row = identity),
    /// so the Cholesky only needs to be rebuilt when the pinned SET changes.
    ///
    /// FORCED (dragged / animation) joints are NOT in the system matrix;
    /// their positions are simply overridden in the iterate after the linear
    /// solve.  This keeps the factorization stable across frames.
    /// </summary>
    public class FMSSolver {

        // ── Dimensions ─────────────────────────────────────────────────────────
        public readonly int n; // joints
        public readonly int s; // springs

        // ── Spring data ────────────────────────────────────────────────────────
        readonly int[]   si, sj;   // spring endpoint indices
        readonly float[] restLen;  // rest length r_k (world-space)
        readonly float[] kk;       // stiffness k_k

        // ── Timestep ──────────────────────────────────────────────────────────
        readonly float h;   // fixed timestep
        readonly float hh;  // h²

        // ── Mass (diagonal M) ─────────────────────────────────────────────────
        readonly float[] mass; // length n

        // ── Precomputed matrices ───────────────────────────────────────────────
        float[] Lmat;   // n×n Laplacian (weighted by k_k)
        float[] Abase;  // n×n  M + h²·L   (no pin modifications)
        float[] cholL;  // lower-triangular Cholesky factor of modified A
        bool    cholDirty = true;

        // ── Pinned set ────────────────────────────────────────────────────────
        bool[] pinned; // pinned[i] = true → row i replaced by identity in A

        // ── Simulation state ──────────────────────────────────────────────────
        public float[] posX, posY, posZ;
        public float[] velX, velY, velZ;

        // ── Local-step auxiliary d vectors ────────────────────────────────────
        float[] dX, dY, dZ; // length s

        // ── Rest Stiffness ────────────────────────────────────────────────────
        float _restStiffness = 0f;
        public float RestStiffness {
            get => _restStiffness;
            set {
                if (Mathf.Abs(_restStiffness - value) > 1e-6f) {
                    _restStiffness = value;
                    cholDirty = true; // matrix depends on rest stiffness
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Construction
        // ─────────────────────────────────────────────────────────────────────

        public FMSSolver(int n, int s,
                         int[] si, int[] sj,
                         float[] restLengths, float[] stiffnesses,
                         float nodeMass, float timestep) {
            this.n  = n;  this.s  = s;
            this.h  = timestep;  this.hh = timestep * timestep;
            this.si = si;  this.sj = sj;
            restLen = restLengths;
            kk      = stiffnesses;

            posX = new float[n]; posY = new float[n]; posZ = new float[n];
            velX = new float[n]; velY = new float[n]; velZ = new float[n];
            dX   = new float[s]; dY   = new float[s]; dZ   = new float[s];
            pinned = new bool[n];

            mass = new float[n];
            for (int i = 0; i < n; i++) mass[i] = nodeMass;

            BuildBaseMatrices();
            // cholL is built lazily on first Step()
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Seed positions from a List (resets velocities to zero).</summary>
        public void SetPositionsFromList(System.Collections.Generic.List<Vector3> positions) {
            for (int i = 0; i < n; i++) {
                posX[i] = positions[i].x;
                posY[i] = positions[i].y;
                posZ[i] = positions[i].z;
                velX[i] = velY[i] = velZ[i] = 0f;
            }
        }

        /// <summary>
        /// Overwrite positions from a List WITHOUT touching velocities.
        /// Use this to sync back PBD-corrected positions so the solver starts
        /// the next frame from the corrected state while preserving momentum.
        /// </summary>
        public void OverridePositions(System.Collections.Generic.List<Vector3> positions) {
            for (int i = 0; i < n; i++) {
                posX[i] = positions[i].x;
                posY[i] = positions[i].y;
                posZ[i] = positions[i].z;
            }
        }

        /// <summary>
        /// Notify that the pinned set changed.
        /// Triggers a Cholesky rebuild on the next Step().
        /// </summary>
        public void SetPinned(bool[] pinnedMask) {
            bool changed = false;
            for (int i = 0; i < n; i++) {
                if (pinned[i] != pinnedMask[i]) { changed = true; break; }
            }
            if (!changed) return;
            Array.Copy(pinnedMask, pinned, n);
            cholDirty = true;
        }

        /// <summary>
        /// One simulation step.
        /// </summary>
        /// <param name="iterations">Local/global iteration count.</param>
        /// <param name="gravityY">Downward acceleration (negative = down, e.g. -9.8).</param>
        /// <param name="damping">Velocity damping per step, [0,1). 0 = no damping, 0.5 = 50% loss per step.</param>
        /// <param name="forcedIndices">Joints whose position is externally driven this frame (dragged/animated). May be null.</param>
        /// <param name="forcedPositions">World positions for each forced joint.</param>
        /// <param name="restPositions">Rest (natural) world positions for each joint. May be null (disables restoring force).</param>
        /// <param name="restStiffness">Stiffness of the Hooke restoring force pulling each free joint back to its rest position.</param>
        public void Step(int     iterations,
                         float   gravityY,
                         float   damping,
                         int[]   forcedIndices,
                         Vector3[] forcedPositions,
                         Vector3[] restPositions  = null,
                         float   restStiffness    = 0f) {

            // ── Rebuild Cholesky if pinned set changed ─────────────────────────
            if (cholDirty) {
                RebuildCholesky();
                cholDirty = false;
            }

            // ── 1. Damp velocities ────────────────────────────────────────────
            float keep = 1f - Mathf.Clamp01(damping);
            for (int i = 0; i < n; i++) {
                velX[i] *= keep;
                velY[i] *= keep;
                velZ[i] *= keep;
            }

            // ── 2. Inertia target: y = x + h·v + h²·fext/m ────────────────────
            // Build a fast lookup: is joint i forced this frame?
            bool[] isForcedThisFrame = new bool[n];
            if (forcedIndices != null)
                foreach (int fi in forcedIndices)
                    if (fi >= 0 && fi < n) isForcedThisFrame[fi] = true;

            float[] yX = new float[n], yY = new float[n], yZ = new float[n];
            for (int i = 0; i < n; i++) {
                yX[i] = posX[i] + h * velX[i];
                yY[i] = posY[i] + h * velY[i] + hh * gravityY;
                yZ[i] = posZ[i] + h * velZ[i];

                // Explicit rest-position force has been removed.
                // Restoring force is now handled implicitly via the Cholesky matrix
                // and GlobalStep RHS for unconditional stability.
            }

            // Pin joints are already in y at rest (solver keeps them there via identity rows).
            // Forced joints: seed y so the iterate starts at the right place.
            OverrideForced(forcedIndices, forcedPositions, yX, yY, yZ);

            // ── 3. Initial iterate x* ← y ─────────────────────────────────────
            float[] xX = (float[])yX.Clone();
            float[] xY = (float[])yY.Clone();
            float[] xZ = (float[])yZ.Clone();

            // ── 4. Local / Global iterations ──────────────────────────────────
            for (int iter = 0; iter < iterations; iter++) {
                LocalStep(xX, xY, xZ);
                GlobalStep(yX, yY, yZ, ref xX, ref xY, ref xZ, restPositions);

                // Override forced joint positions AFTER global solve
                OverrideForced(forcedIndices, forcedPositions, xX, xY, xZ);
            }

            // ── 5. Velocity update: v = (x* − x) / h ─────────────────────────
            float invH = 1f / h;
            for (int i = 0; i < n; i++) {
                velX[i] = (xX[i] - posX[i]) * invH;
                velY[i] = (xY[i] - posY[i]) * invH;
                velZ[i] = (xZ[i] - posZ[i]) * invH;
            }

            // ── 6. Zero velocity on constrained joints, commit ─────────────────
            for (int i = 0; i < n; i++) {
                if (pinned[i]) { velX[i] = velY[i] = velZ[i] = 0f; }
            }
            if (forcedIndices != null) {
                for (int fi = 0; fi < forcedIndices.Length; fi++) {
                    int idx = forcedIndices[fi];
                    if (idx < 0 || idx >= n) continue;
                    velX[idx] = velY[idx] = velZ[idx] = 0f;
                }
            }

            Array.Copy(xX, posX, n);
            Array.Copy(xY, posY, n);
            Array.Copy(xZ, posZ, n);

            // Final pin: ensure exact positions (eliminates float drift)
            for (int i = 0; i < n; i++) {
                if (pinned[i]) {
                    // pinned rest positions come in via forcedPositions from Puppet
                    // (they are added to forcedIndices on the Puppet side)
                }
            }
            OverrideForced(forcedIndices, forcedPositions, posX, posY, posZ);
        }

        /// <summary>Maximum squared velocity of any free joint (for sleep check).</summary>
        public float MaxVelocitySqr() {
            float max = 0f;
            for (int i = 0; i < n; i++) {
                if (pinned[i]) continue;
                float v = velX[i] * velX[i] + velY[i] * velY[i] + velZ[i] * velZ[i];
                if (v > max) max = v;
            }
            return max;
        }

        /// <summary>Zero all velocities (call on wake to avoid ghost impulse).</summary>
        public void ResetVelocities() {
            for (int i = 0; i < n; i++) velX[i] = velY[i] = velZ[i] = 0f;
        }

        /// <summary>Read back position of joint i.</summary>
        public Vector3 GetPosition(int i) => new Vector3(posX[i], posY[i], posZ[i]);

        // ─────────────────────────────────────────────────────────────────────
        // Internal: matrix construction
        // ─────────────────────────────────────────────────────────────────────

        void BuildBaseMatrices() {
            Lmat  = new float[n * n];
            Abase = new float[n * n];

            for (int k = 0; k < s; k++) {
                int   i  = si[k], j = sj[k];
                float w  = kk[k];
                Lmat[i * n + i] += w;
                Lmat[j * n + j] += w;
                Lmat[i * n + j] -= w;
                Lmat[j * n + i] -= w;
            }

            // A = M + h²·L
            for (int i = 0; i < n; i++) {
                for (int j = 0; j < n; j++)
                    Abase[i * n + j] = hh * Lmat[i * n + j];
                Abase[i * n + i] += mass[i];
            }
        }

        /// <summary>
        /// Build modified A embedding ONLY pinned constraints (identity rows),
        /// and incorporating the implicit rest restoring force on the diagonal.
        /// Forced/dragged joints are NOT in the matrix — they are overridden after solve.
        /// </summary>
        void RebuildCholesky() {
            float[] Amod = (float[])Abase.Clone();
            for (int i = 0; i < n; i++) {
                if (pinned[i]) {
                    for (int j = 0; j < n; j++) {
                        Amod[i * n + j] = 0f;
                        Amod[j * n + i] = 0f;
                    }
                    Amod[i * n + i] = 1f;
                } else {
                    // Implicit rest spring force: adds h²·k_rest to the diagonal
                    Amod[i * n + i] += hh * _restStiffness;
                }
            }
            cholL = CholeskyDecompose(Amod, n);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internal: simulation steps
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Local step: d_k = r_k · normalize(p_i − p_j).
        /// Projects each spring onto its rest length direction.
        /// </summary>
        void LocalStep(float[] px, float[] py, float[] pz) {
            for (int k = 0; k < s; k++) {
                int   i  = si[k], j = sj[k];
                float dx = px[i] - px[j];
                float dy = py[i] - py[j];
                float dz = pz[i] - pz[j];
                float len = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len < 1e-7f) { dX[k] = dY[k] = dZ[k] = 0f; continue; }
                float scale = restLen[k] / len;
                dX[k] = dx * scale;
                dY[k] = dy * scale;
                dZ[k] = dz * scale;
            }
        }

        /// <summary>
        /// Global step: solve (M + h²L) x = M·y + h²·J·d per axis.
        /// Rows of pinned joints are replaced by x_i = y_i (rest position).
        /// Rest force adds h²·k_rest·p_rest to RHS for free joints.
        /// </summary>
        void GlobalStep(float[] yX, float[] yY, float[] yZ,
                        ref float[] xX, ref float[] xY, ref float[] xZ,
                        Vector3[] restPositions = null) {
            // RHS = M·y
            float[] rhsX = new float[n], rhsY = new float[n], rhsZ = new float[n];
            for (int i = 0; i < n; i++) {
                rhsX[i] = mass[i] * yX[i];
                rhsY[i] = mass[i] * yY[i];
                rhsZ[i] = mass[i] * yZ[i];
            }

            // Add h²·J·d
            for (int k = 0; k < s; k++) {
                int   i  = si[k], j = sj[k];
                float hkX = hh * kk[k] * dX[k];
                float hkY = hh * kk[k] * dY[k];
                float hkZ = hh * kk[k] * dZ[k];
                rhsX[i] += hkX; rhsY[i] += hkY; rhsZ[i] += hkZ;
                rhsX[j] -= hkX; rhsY[j] -= hkY; rhsZ[j] -= hkZ;
            }

            // Add h²·k_rest·p_rest for free joints (implicit rest force)
            if (_restStiffness > 0f && restPositions != null) {
                float hkRest = hh * _restStiffness;
                for (int i = 0; i < n; i++) {
                    if (!pinned[i]) {
                        rhsX[i] += hkRest * restPositions[i].x;
                        rhsY[i] += hkRest * restPositions[i].y;
                        rhsZ[i] += hkRest * restPositions[i].z;
                    }
                }
            }

            // Correction for zeroed columns in A matrix (Pinned joints)
            // Since we set A_{j,i} = 0 for pinned joint i to keep A symmetric,
            // we must move its contribution A_{j,i} * x_i to the RHS.
            // A_{j,i} = -h² * k, and x_i = y_i, so we add h² * k * y_i to rhs_j.
            for (int k = 0; k < s; k++) {
                int i = si[k], j = sj[k];
                bool pinI = pinned[i], pinJ = pinned[j];
                if (pinI && !pinJ) {
                    float hk = hh * kk[k];
                    rhsX[j] += hk * yX[i];
                    rhsY[j] += hk * yY[i];
                    rhsZ[j] += hk * yZ[i];
                } else if (!pinI && pinJ) {
                    float hk = hh * kk[k];
                    rhsX[i] += hk * yX[j];
                    rhsY[i] += hk * yY[j];
                    rhsZ[i] += hk * yZ[j];
                }
            }

            // Pinned rows → x_i = y_i  (A has identity row, so RHS must equal target)
            for (int i = 0; i < n; i++) {
                if (!pinned[i]) continue;
                rhsX[i] = yX[i];
                rhsY[i] = yY[i];
                rhsZ[i] = yZ[i];
            }

            // Solve
            xX = CholeskySolve(rhsX);
            xY = CholeskySolve(rhsY);
            xZ = CholeskySolve(rhsZ);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Dense Cholesky (suitable for small n, typ. 5–30 skeleton joints)
        // ─────────────────────────────────────────────────────────────────────

        static float[] CholeskyDecompose(float[] A, int n) {
            float[] L = new float[n * n];
            for (int i = 0; i < n; i++) {
                for (int j = 0; j <= i; j++) {
                    float sum = A[i * n + j];
                    for (int k = 0; k < j; k++)
                        sum -= L[i * n + k] * L[j * n + k];
                    if (i == j)
                        L[i * n + i] = Mathf.Sqrt(Mathf.Max(sum, 1e-12f));
                    else {
                        float d = L[j * n + j];
                        L[i * n + j] = d > 1e-12f ? sum / d : 0f;
                    }
                }
            }
            return L;
        }

        static float[] ForwardSub(float[] L, float[] b, int n) {
            float[] y = new float[n];
            for (int i = 0; i < n; i++) {
                float s = b[i];
                for (int j = 0; j < i; j++) s -= L[i * n + j] * y[j];
                float d = L[i * n + i];
                y[i] = d > 1e-12f ? s / d : 0f;
            }
            return y;
        }

        static float[] BackSub(float[] L, float[] y, int n) {
            float[] x = new float[n];
            for (int i = n - 1; i >= 0; i--) {
                float s = y[i];
                for (int j = i + 1; j < n; j++) s -= L[j * n + i] * x[j];
                float d = L[i * n + i];
                x[i] = d > 1e-12f ? s / d : 0f;
            }
            return x;
        }

        float[] CholeskySolve(float[] b) =>
            BackSub(cholL, ForwardSub(cholL, b, n), n);

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        static void OverrideForced(int[] indices, Vector3[] positions,
                                    float[] px, float[] py, float[] pz) {
            if (indices == null) return;
            for (int fi = 0; fi < indices.Length; fi++) {
                int idx = indices[fi];
                if (idx < 0 || idx >= px.Length) continue;
                px[idx] = positions[fi].x;
                py[idx] = positions[fi].y;
                pz[idx] = positions[fi].z;
            }
        }
    }
}
