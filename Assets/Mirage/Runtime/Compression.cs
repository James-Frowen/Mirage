using System;
using UnityEngine;

namespace Mirage
{
    enum ComponentType : uint
    {
        X = 0,
        Y = 1,
        Z = 2,
        W = 3
    }

    /// <summary>
    ///     Credit to this man for converting gaffer games c code to c#
    ///     https://gist.github.com/fversnel/0497ad7ab3b81e0dc1dd
    /// </summary>
    [System.Obsolete("Replace with bitpacking")]
    public static class Compression
    {
        private const float Minimum = -1.0f / 1.414214f; // note: 1.0f / sqrt(2)
        private const float Maximum = +1.0f / 1.414214f;

        internal static uint Compress(Quaternion quaternion)
        {
            throw new NotImplementedException();
        }

        internal static Quaternion Decompress(uint compressed)
        {
            throw new NotImplementedException();
        }
    }
}
