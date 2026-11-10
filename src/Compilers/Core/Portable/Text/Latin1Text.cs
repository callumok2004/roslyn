// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Text;

namespace Microsoft.CodeAnalysis.Text
{
    /// <summary>
    /// A <see cref="SourceText"/> whose characters are all below U+0100, stored one byte per character.
    /// Behaves identically to <see cref="StringText"/> for the same characters while using half the memory.
    /// </summary>
    internal sealed class Latin1Text : SourceText
    {
        private readonly byte[] _bytes;
        private readonly Encoding? _encodingOpt;

        private Latin1Text(byte[] bytes, Encoding? encodingOpt, ImmutableArray<byte> checksum, SourceHashAlgorithm checksumAlgorithm, ImmutableArray<byte> embeddedTextBlob)
            : base(checksum, checksumAlgorithm, embeddedTextBlob)
        {
            _bytes = bytes;
            _encodingOpt = encodingOpt;
        }

        /// <summary>
        /// Creates a <see cref="Latin1Text"/> when every character fits in a byte, otherwise a <see cref="StringText"/>.
        /// </summary>
        internal static SourceText Create(string source, Encoding? encodingOpt, ImmutableArray<byte> checksum, SourceHashAlgorithm checksumAlgorithm, ImmutableArray<byte> embeddedTextBlob)
        {
            foreach (var c in source)
            {
                if (c > 'ÿ')
                    return new StringText(source, encodingOpt, checksum, checksumAlgorithm, embeddedTextBlob);
            }

            var bytes = new byte[source.Length];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)source[i];

            return new Latin1Text(bytes, encodingOpt, checksum, checksumAlgorithm, embeddedTextBlob);
        }

        public override Encoding? Encoding => _encodingOpt;

        public override int Length => _bytes.Length;

        public override char this[int position] => (char)_bytes[position];

        public override string ToString(TextSpan span)
        {
            if (span.End > _bytes.Length)
                throw new ArgumentOutOfRangeException(nameof(span));

#if NET
            return string.Create(span.Length, (_bytes, span.Start), static (chars, state) => Widen(state._bytes.AsSpan(state.Start, chars.Length), chars));
#else
            var chars = new char[span.Length];
            Widen(_bytes.AsSpan(span.Start, span.Length), chars);
            return new string(chars);
#endif
        }

        public override void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count)
        {
            if (destination is null)
                throw new ArgumentNullException(nameof(destination));

            Widen(_bytes.AsSpan(sourceIndex, count), destination.AsSpan(destinationIndex, count));
        }

        private static void Widen(ReadOnlySpan<byte> source, Span<char> destination)
        {
#if NET
            Encoding.Latin1.GetChars(source, destination);
#else
            for (var i = 0; i < source.Length; i++)
                destination[i] = (char)source[i];
#endif
        }
    }
}
