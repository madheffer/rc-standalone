// Port of lib/lz4hc.c from lz4 1.10.0, the optimal parser only (levels 10 to 12).
// LZ4 HC - High Compression Mode of LZ4. Copyright (c) Yann Collet. All rights reserved.
// BSD 2-Clause License (https://opensource.org/licenses/bsd-license.php):
// Redistribution and use in source and binary forms, with or without modification, are permitted
// provided that the following conditions are met: redistributions of source code must retain the
// above copyright notice, this list of conditions and the following disclaimer; redistributions in
// binary form must reproduce the above copyright notice, this list of conditions and the following
// disclaimer in the documentation and/or other materials provided with the distribution.
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
// IMPLIED WARRANTIES ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE
// FOR ANY DAMAGES ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE.

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Source2.Compiler.Compression;

/// <summary>
/// An LZ4 HC stream, as lz4 1.10.0's <c>LZ4_streamHC_t</c> driven by <c>LZ4_compress_HC_continue</c>,
/// for the optimal parser levels (10 to 12). tier0 compresses KV3 with lz4 1.9.3 or later: K4os ports
/// 1.9.2, whose match finder differs once a stream's input stops being contiguous (the 0x14000-byte
/// blob ring wrapping), and 1.9.3, 1.9.4 and 1.10.0 all reproduce Valve's frames there (63 of 63 stock
/// clips that 1.9.2 gets wrong). Follows the C closely, pointers included, so that it can be checked
/// line by line against <c>lz4hc.c</c>.
/// </summary>
internal sealed unsafe class Lz4HcStream : IDisposable
{
    private const int MinMatch = 4, MfLimit = 12, LastLiterals = 5;
    private const int MlBits = 4, MlMask = (1 << MlBits) - 1, RunMask = (1 << (8 - MlBits)) - 1;
    private const int DistanceMax = 65535, HashLog = 15, HashTableSize = 1 << HashLog, ChainSize = 1 << 16;
    private const int OptNum = 1 << 12, TrailingLiterals = 3, MaxInputSize = 0x7E000000;
    private const uint Kb64 = 64 * 1024, Gb1 = 1u << 30;

    private struct Optimal { public int Price, Off, MLen, LitLen; }
    private readonly struct Match(int off, int len, int back) { public readonly int Off = off, Len = len, Back = back; }

    private readonly uint* _hashTable = (uint*)NativeMemory.AllocZeroed(HashTableSize * sizeof(uint));
    private readonly ushort* _chainTable = (ushort*)NativeMemory.AllocZeroed(ChainSize * sizeof(ushort));
    private readonly Optimal* _opt = (Optimal*)NativeMemory.AllocZeroed((OptNum + TrailingLiterals) * (nuint)sizeof(Optimal));
    private byte* _end, _prefixStart, _dictStart;
    private uint _dictLimit, _lowLimit, _nextToUpdate;
    private readonly int _level, _nbSearches, _targetLength;

    /// <summary>A fresh stream (<c>LZ4_initStreamHC</c> then <c>LZ4_setCompressionLevel</c>).</summary>
    public Lz4HcStream(int level = 12)
    {
        _level = Math.Clamp(level, 10, 12);
        (_nbSearches, _targetLength) = _level switch { 10 => (96, 64), 11 => (512, 128), _ => (16384, OptNum) };
    }

    public void Dispose()
    {
        NativeMemory.Free(_hashTable);
        NativeMemory.Free(_chainTable);
        NativeMemory.Free(_opt);
    }

    /// <summary>The worst-case compressed size of <paramref name="size"/> bytes (<c>LZ4_compressBound</c>).</summary>
    public static int CompressBound(int size) => (uint)size > MaxInputSize ? 0 : size + size / 255 + 16;

    /// <summary>
    /// <c>LZ4_compress_HC</c>: one block with a fresh state. Returns the compressed size, or 0 when
    /// <paramref name="dst"/> is too small.
    /// </summary>
    public static int Compress(ReadOnlySpan<byte> src, Span<byte> dst, int level = 12)
    {
        using var s = new Lz4HcStream(level);
        fixed (byte* ps = src)
        fixed (byte* pd = dst)
        {
            s.InitInternal(ps);
            return s.CompressGeneric(ps, pd, src.Length, dst.Length);
        }
    }

    /// <summary>
    /// <c>LZ4_compress_HC_continue</c>: compress <paramref name="srcSize"/> bytes at <paramref name="src"/>,
    /// which may refer to every earlier input of this stream still in memory at the same address (the
    /// previous block, or the one before it when this one does not follow on). The caller keeps that
    /// memory in place.
    /// </summary>
    public int CompressContinue(byte* src, byte* dst, int srcSize, int dstCapacity)
    {
        if (_prefixStart == null)
            InitInternal(src);
        if ((ulong)(_end - _prefixStart) + _dictLimit > 2 * Gb1)
            throw new NotSupportedException("LZ4 HC stream past 2 GB");
        if (src != _end)
            SetExternalDict(src);

        // Check overlapping input/dictionary space
        var sourceEnd = src + srcSize;
        var dictBegin = _dictStart;
        var dictEnd = _dictStart + (_dictLimit - _lowLimit);
        if (sourceEnd > dictBegin && src < dictEnd)
        {
            if (sourceEnd > dictEnd) sourceEnd = dictEnd;
            _lowLimit += (uint)(sourceEnd - _dictStart);
            _dictStart += (uint)(sourceEnd - _dictStart);
            // invalidate dictionary is it's too small
            if (_dictLimit - _lowLimit < 4)
            {
                _lowLimit = _dictLimit;
                _dictStart = _prefixStart;
            }
        }
        return CompressGeneric(src, dst, srcSize, dstCapacity);
    }

    private int CompressGeneric(byte* src, byte* dst, int srcSize, int dstCapacity)
    {
        if ((uint)srcSize > MaxInputSize) return 0;
        _end += srcSize;
        var limited = dstCapacity < CompressBound(srcSize);
        return CompressOptimal(src, dst, srcSize, dstCapacity, _nbSearches, _targetLength, limited, fullUpdate: _level >= 12);
    }

    private void InitInternal(byte* start)
    {
        var bufferSize = (ulong)(_end - _prefixStart);
        var newStartingOffset = bufferSize + _dictLimit;
        if (newStartingOffset > Gb1)
        {
            NativeMemory.Clear(_hashTable, HashTableSize * sizeof(uint));
            NativeMemory.Fill(_chainTable, ChainSize * sizeof(ushort), 0xFF);
            newStartingOffset = 0;
        }
        newStartingOffset += Kb64;
        _nextToUpdate = (uint)newStartingOffset;
        _prefixStart = start;
        _end = start;
        _dictStart = start;
        _dictLimit = (uint)newStartingOffset;
        _lowLimit = (uint)newStartingOffset;
    }

    private void SetExternalDict(byte* newBlock)
    {
        if (_end >= _prefixStart + 4)
            Insert(_end - 3);   // Referencing remaining dictionary content
        // Only one memory segment for extDict, so any previous extDict is lost at this stage
        _lowLimit = _dictLimit;
        _dictStart = _prefixStart;
        _dictLimit += (uint)(_end - _prefixStart);
        _prefixStart = newBlock;
        _end = newBlock;
        _nextToUpdate = _dictLimit;   // match referencing will resume from there
    }

    private static uint Read32(byte* p) => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(p, 4));
    private static ushort Read16(byte* p) => BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(p, 2));
    private static uint HashPtr(byte* p) => (Read32(p) * 2654435761U) >> (MinMatch * 8 - HashLog);
    private ushort Next(uint index) => _chainTable[(ushort)index];

    // LZ4_count: equal bytes from pIn and pMatch, stopping at pInLimit.
    private static int Count(byte* pIn, byte* pMatch, byte* pInLimit)
    {
        var start = pIn;
        while (pIn < pInLimit && *pIn == *pMatch) { pIn++; pMatch++; }
        return (int)(pIn - start);
    }

    // LZ4HC_countBack: negative, the equal bytes before ip and match.
    private static int CountBack(byte* ip, byte* match, byte* iMin, byte* mMin)
    {
        var back = 0;
        var min = (int)Math.Max(iMin - ip, mMin - match);
        while (back > min && ip[back - 1] == match[back - 1]) back--;
        return back;
    }

    // Update chains up to ip (excluded)
    private void Insert(byte* ip)
    {
        var prefixPtr = _prefixStart;
        var prefixIdx = _dictLimit;
        var target = (uint)(ip - prefixPtr) + prefixIdx;
        var idx = _nextToUpdate;
        while (idx < target)
        {
            var h = HashPtr(prefixPtr + ((long)idx - prefixIdx));
            var delta = (ulong)(idx - _hashTable[h]);
            if (delta > DistanceMax) delta = DistanceMax;
            _chainTable[(ushort)idx] = (ushort)delta;
            _hashTable[h] = idx;
            idx++;
        }
        _nextToUpdate = target;
    }

    private static uint RotatePattern(ulong rotate, uint pattern)
    {
        var bitsToRotate = (int)((rotate & 3) << 3);
        return bitsToRotate == 0 ? pattern : (pattern << bitsToRotate) | (pattern >> (32 - bitsToRotate));
    }

    // LZ4HC_countPattern: pattern32 is a sample of a repetitive pattern of length 1, 2 or 4.
    private static ulong CountPattern(byte* ip, byte* iEnd, uint pattern32)
    {
        var start = ip;
        while (ip < iEnd && *ip == (byte)(pattern32 >> (int)(8 * ((ip - start) & 3)))) ip++;
        return (ulong)(ip - start);
    }

    // LZ4HC_reverseCountPattern: pattern read in little-endian order.
    private static ulong ReverseCountPattern(byte* ip, byte* iLow, uint pattern)
    {
        var start = ip;
        while (ip >= iLow + 4)
        {
            if (Read32(ip - 4) != pattern) break;
            ip -= 4;
        }
        var bytePos = 3;
        while (ip > iLow)
        {
            if (ip[-1] != (byte)(pattern >> (8 * bytePos))) break;
            ip--; bytePos--;
        }
        return (ulong)(start - ip);
    }

    // Whether matchIndex is clear of the last 3 bytes of the dictionary (a 4-byte read would overflow).
    private static bool ProtectDictEnd(uint dictLimit, uint matchIndex) => (dictLimit - 1 - matchIndex) >= 3;

    private Match InsertAndGetWiderMatch(byte* ip, byte* iLowLimit, byte* iHighLimit, int longest,
        int maxNbAttempts, bool patternAnalysis, bool chainSwap)
    {
        var prefixPtr = _prefixStart;
        var prefixIdx = _dictLimit;
        var ipIndex = (uint)(ip - prefixPtr) + prefixIdx;
        var withinStartDistance = _lowLimit + (DistanceMax + 1) > ipIndex;
        var lowestMatchIndex = withinStartDistance ? _lowLimit : ipIndex - DistanceMax;
        var dictStart = _dictStart;
        var dictIdx = _lowLimit;
        var dictEnd = dictStart + (prefixIdx - dictIdx);
        var lookBackLength = (int)(ip - iLowLimit);
        var nbAttempts = maxNbAttempts;
        uint matchChainPos = 0;
        var pattern = Read32(ip);
        var repeat = 0;   // 0 untested, 1 not, 2 confirmed
        ulong srcPatternLength = 0;
        int offset = 0, sBack = 0;

        Insert(ip);   // insert all prior positions up to ip (excluded)
        var matchIndex = _hashTable[HashPtr(ip)];

        while (matchIndex >= lowestMatchIndex && nbAttempts > 0)
        {
            var matchLength = 0;
            nbAttempts--;
            if (matchIndex >= prefixIdx)
            {
                // within current Prefix
                var matchPtr = prefixPtr + (matchIndex - prefixIdx);
                if (Read16(iLowLimit + longest - 1) == Read16(matchPtr - lookBackLength + longest - 1)
                    && Read32(matchPtr) == pattern)
                {
                    var back = lookBackLength != 0 ? CountBack(ip, matchPtr, iLowLimit, prefixPtr) : 0;
                    matchLength = MinMatch + Count(ip + MinMatch, matchPtr + MinMatch, iHighLimit);
                    matchLength -= back;
                    if (matchLength > longest)
                    {
                        longest = matchLength;
                        offset = (int)(ipIndex - matchIndex);
                        sBack = back;
                    }
                }
            }
            else
            {
                // lowestMatchIndex <= matchIndex < dictLimit : within Ext Dict
                var matchPtr = dictStart + (matchIndex - dictIdx);
                if (matchIndex <= prefixIdx - 4 && Read32(matchPtr) == pattern)
                {
                    var vLimit = ip + (prefixIdx - matchIndex);
                    if (vLimit > iHighLimit) vLimit = iHighLimit;
                    matchLength = Count(ip + MinMatch, matchPtr + MinMatch, vLimit) + MinMatch;
                    if (ip + matchLength == vLimit && vLimit < iHighLimit)
                        matchLength += Count(ip + matchLength, prefixPtr, iHighLimit);
                    var back = lookBackLength != 0 ? CountBack(ip, matchPtr, iLowLimit, dictStart) : 0;
                    matchLength -= back;
                    if (matchLength > longest)
                    {
                        longest = matchLength;
                        offset = (int)(ipIndex - matchIndex);
                        sBack = back;
                    }
                }
            }

            if (chainSwap && matchLength == longest && matchIndex + (uint)longest <= ipIndex)
            {
                // better match => select a better chain
                const int kTrigger = 4;
                uint distanceToNextMatch = 1;
                var end = longest - MinMatch + 1;
                var step = 1;
                var accel = 1 << kTrigger;
                for (var pos = 0; pos < end; pos += step)
                {
                    uint candidateDist = Next(matchIndex + (uint)pos);
                    step = accel++ >> kTrigger;
                    if (candidateDist > distanceToNextMatch)
                    {
                        distanceToNextMatch = candidateDist;
                        matchChainPos = (uint)pos;
                        accel = 1 << kTrigger;
                    }
                }
                if (distanceToNextMatch > 1)
                {
                    if (distanceToNextMatch > matchIndex) break;   // avoid overflow
                    matchIndex -= distanceToNextMatch;
                    continue;
                }
            }

            uint distNextMatch = Next(matchIndex);
            if (patternAnalysis && distNextMatch == 1 && matchChainPos == 0)
            {
                var matchCandidateIdx = matchIndex - 1;
                // may be a repeated pattern
                if (repeat == 0)
                {
                    if (((pattern & 0xFFFF) == (pattern >> 16)) & ((pattern & 0xFF) == (pattern >> 24)))
                    {
                        repeat = 2;
                        srcPatternLength = CountPattern(ip + 4, iHighLimit, pattern) + 4;
                    }
                    else
                    {
                        repeat = 1;
                    }
                }
                if (repeat == 2 && matchCandidateIdx >= lowestMatchIndex && ProtectDictEnd(prefixIdx, matchCandidateIdx))
                {
                    var extDict = matchCandidateIdx < prefixIdx;
                    var matchPtr = extDict ? dictStart + (matchCandidateIdx - dictIdx) : prefixPtr + (matchCandidateIdx - prefixIdx);
                    if (Read32(matchPtr) == pattern)
                    {
                        // good candidate
                        var iLimit = extDict ? dictEnd : iHighLimit;
                        var forwardPatternLength = CountPattern(matchPtr + 4, iLimit, pattern) + 4;
                        if (extDict && matchPtr + forwardPatternLength == iLimit)
                        {
                            var rotatedPattern = RotatePattern(forwardPatternLength, pattern);
                            forwardPatternLength += CountPattern(prefixPtr, iHighLimit, rotatedPattern);
                        }
                        var lowestMatchPtr = extDict ? dictStart : prefixPtr;
                        var backLength = ReverseCountPattern(matchPtr, lowestMatchPtr, pattern);
                        if (!extDict && matchPtr - backLength == prefixPtr && dictIdx < prefixIdx)
                        {
                            var rotatedPattern = RotatePattern((uint)-(int)backLength, pattern);
                            backLength += ReverseCountPattern(dictEnd, dictStart, rotatedPattern);
                        }
                        // Limit backLength not go further than lowestMatchIndex
                        backLength = matchCandidateIdx - Math.Max(matchCandidateIdx - (uint)backLength, lowestMatchIndex);
                        var currentSegmentLength = backLength + forwardPatternLength;
                        // Adjust to end of pattern if the source pattern fits, otherwise the beginning of the pattern
                        if (currentSegmentLength >= srcPatternLength && forwardPatternLength <= srcPatternLength)
                        {
                            // best position, full pattern, might be followed by more match
                            var newMatchIndex = matchCandidateIdx + (uint)forwardPatternLength - (uint)srcPatternLength;
                            matchIndex = ProtectDictEnd(prefixIdx, newMatchIndex) ? newMatchIndex : prefixIdx;
                        }
                        else
                        {
                            // farthest position in current segment, will find a match of length currentSegmentLength + maybe some back
                            var newMatchIndex = matchCandidateIdx - (uint)backLength;
                            if (!ProtectDictEnd(prefixIdx, newMatchIndex))
                            {
                                matchIndex = prefixIdx;
                            }
                            else
                            {
                                matchIndex = newMatchIndex;
                                if (lookBackLength == 0)
                                {
                                    // no back possible
                                    var maxMl = Math.Min(currentSegmentLength, srcPatternLength);
                                    if ((ulong)longest < maxMl)
                                    {
                                        if ((ulong)(ip - prefixPtr) + prefixIdx - matchIndex > DistanceMax) break;
                                        longest = (int)maxMl;
                                        offset = (int)(ipIndex - matchIndex);
                                    }
                                    uint distToNextPattern = Next(matchIndex);
                                    if (distToNextPattern > matchIndex) break;   // avoid overflow
                                    matchIndex -= distToNextPattern;
                                }
                            }
                        }
                        continue;
                    }
                }
            }

            // follow current chain
            matchIndex -= Next(matchIndex + matchChainPos);
        }

        return new Match(offset, longest, sBack);
    }

    private Match FindLongerMatch(byte* ip, byte* iHighLimit, int minLen, int nbSearches)
    {
        var md = InsertAndGetWiderMatch(ip, ip, iHighLimit, minLen, nbSearches, patternAnalysis: true, chainSwap: true);
        return md.Len <= minLen ? default : md;
    }

    private static int LiteralsPrice(int litlen)
    {
        var price = litlen;
        if (litlen >= RunMask)
            price += 1 + (litlen - RunMask) / 255;
        return price;
    }

    // requires mlen >= MINMATCH
    private static int SequencePrice(int litlen, int mlen)
    {
        var price = 1 + 2;   // token + 16-bit offset
        price += LiteralsPrice(litlen);
        if (mlen >= MlMask + MinMatch)
            price += 1 + (mlen - (MlMask + MinMatch)) / 255;
        return price;
    }

    // LZ4HC_encodeSequence: false when the output limit is hit.
    private static bool EncodeSequence(ref byte* ip, ref byte* op, ref byte* anchor, int matchLength, int offset, bool limit, byte* oend)
    {
        var token = op++;
        var length = (ulong)(ip - anchor);
        if (limit && op + length / 255 + length + (2 + 1 + LastLiterals) > oend)
            return false;
        if (length >= RunMask)
        {
            var len = length - RunMask;
            *token = RunMask << MlBits;
            for (; len >= 255; len -= 255) *op++ = 255;
            *op++ = (byte)len;
        }
        else
        {
            *token = (byte)(length << MlBits);
        }

        // Copy Literals
        Buffer.MemoryCopy(anchor, op, (long)length, (long)length);
        op += length;

        // Encode Offset
        *op++ = (byte)offset;
        *op++ = (byte)(offset >> 8);

        // Encode MatchLength
        length = (ulong)(matchLength - MinMatch);
        if (limit && op + length / 255 + (1 + LastLiterals) > oend)
            return false;
        if (length >= MlMask)
        {
            *token += MlMask;
            length -= MlMask;
            for (; length >= 510; length -= 510) { *op++ = 255; *op++ = 255; }
            if (length >= 255) { length -= 255; *op++ = 255; }
            *op++ = (byte)length;
        }
        else
        {
            *token += (byte)length;
        }

        // Prepare next loop
        ip += matchLength;
        anchor = ip;
        return true;
    }

    private int CompressOptimal(byte* source, byte* dst, int srcSize, int dstCapacity, int nbSearches, int sufficientLen, bool limit, bool fullUpdate)
    {
        var opt = _opt;
        var ip = source;
        var anchor = ip;
        var iend = ip + srcSize;
        var mflimit = iend - MfLimit;
        var matchlimit = iend - LastLiterals;
        var op = dst;
        var oend = op + dstCapacity;

        if (sufficientLen >= OptNum) sufficientLen = OptNum - 1;

        // Main Loop
        while (ip <= mflimit)
        {
            var llen = (int)(ip - anchor);
            int bestMlen, bestOff, cur, lastMatchPos;

            var firstMatch = FindLongerMatch(ip, matchlimit, MinMatch - 1, nbSearches);
            if (firstMatch.Len == 0) { ip++; continue; }

            if (firstMatch.Len > sufficientLen)
            {
                // good enough solution : immediate encoding
                if (!EncodeSequence(ref ip, ref op, ref anchor, firstMatch.Len, firstMatch.Off, limit, oend))
                    return 0;
                continue;
            }

            // set prices for first positions (literals)
            for (var rPos = 0; rPos < MinMatch; rPos++)
            {
                opt[rPos].MLen = 1;
                opt[rPos].Off = 0;
                opt[rPos].LitLen = llen + rPos;
                opt[rPos].Price = LiteralsPrice(llen + rPos);
            }
            // set prices using initial match
            for (var mlen = MinMatch; mlen <= firstMatch.Len; mlen++)
            {
                opt[mlen].MLen = mlen;
                opt[mlen].Off = firstMatch.Off;
                opt[mlen].LitLen = llen;
                opt[mlen].Price = SequencePrice(llen, mlen);
            }
            lastMatchPos = firstMatch.Len;
            for (var addLit = 1; addLit <= TrailingLiterals; addLit++)
            {
                opt[lastMatchPos + addLit].MLen = 1;   // literal
                opt[lastMatchPos + addLit].Off = 0;
                opt[lastMatchPos + addLit].LitLen = addLit;
                opt[lastMatchPos + addLit].Price = opt[lastMatchPos].Price + LiteralsPrice(addLit);
            }

            // check further positions
            var encodeNow = false;
            bestMlen = 0; bestOff = 0;
            for (cur = 1; cur < lastMatchPos; cur++)
            {
                var curPtr = ip + cur;
                if (curPtr > mflimit) break;
                if (fullUpdate)
                {
                    // not useful to search here if next position has same (or lower) cost
                    if (opt[cur + 1].Price <= opt[cur].Price && opt[cur + MinMatch].Price < opt[cur].Price + 3)
                        continue;
                }
                else if (opt[cur + 1].Price <= opt[cur].Price)
                {
                    continue;
                }

                var newMatch = fullUpdate
                    ? FindLongerMatch(curPtr, matchlimit, MinMatch - 1, nbSearches)
                    : FindLongerMatch(curPtr, matchlimit, lastMatchPos - cur, nbSearches);
                if (newMatch.Len == 0) continue;

                if (newMatch.Len > sufficientLen || newMatch.Len + cur >= OptNum)
                {
                    // immediate encoding
                    bestMlen = newMatch.Len;
                    bestOff = newMatch.Off;
                    lastMatchPos = cur + 1;
                    encodeNow = true;
                    break;
                }

                // before match : set price with literals at beginning
                var baseLitlen = opt[cur].LitLen;
                for (var litlen = 1; litlen < MinMatch; litlen++)
                {
                    var price = opt[cur].Price - LiteralsPrice(baseLitlen) + LiteralsPrice(baseLitlen + litlen);
                    var pos = cur + litlen;
                    if (price < opt[pos].Price)
                    {
                        opt[pos].MLen = 1;   // literal
                        opt[pos].Off = 0;
                        opt[pos].LitLen = baseLitlen + litlen;
                        opt[pos].Price = price;
                    }
                }

                // set prices using match at position = cur
                var matchMl = newMatch.Len;
                for (var ml = MinMatch; ml <= matchMl; ml++)
                {
                    var pos = cur + ml;
                    int price, ll;
                    if (opt[cur].MLen == 1)
                    {
                        ll = opt[cur].LitLen;
                        price = (cur > ll ? opt[cur - ll].Price : 0) + SequencePrice(ll, ml);
                    }
                    else
                    {
                        ll = 0;
                        price = opt[cur].Price + SequencePrice(0, ml);
                    }

                    if (pos > lastMatchPos + TrailingLiterals || price <= opt[pos].Price)
                    {
                        if (ml == matchMl && lastMatchPos < pos)   // last pos of last match
                            lastMatchPos = pos;
                        opt[pos].MLen = ml;
                        opt[pos].Off = newMatch.Off;
                        opt[pos].LitLen = ll;
                        opt[pos].Price = price;
                    }
                }
                // complete following positions with literals
                for (var addLit = 1; addLit <= TrailingLiterals; addLit++)
                {
                    opt[lastMatchPos + addLit].MLen = 1;   // literal
                    opt[lastMatchPos + addLit].Off = 0;
                    opt[lastMatchPos + addLit].LitLen = addLit;
                    opt[lastMatchPos + addLit].Price = opt[lastMatchPos].Price + LiteralsPrice(addLit);
                }
            }

            if (!encodeNow)
            {
                bestMlen = opt[lastMatchPos].MLen;
                bestOff = opt[lastMatchPos].Off;
                cur = lastMatchPos - bestMlen;
            }

            // encode: reverse traversal, looking for the shortest path
            var candidatePos = cur;
            var selectedMatchLength = bestMlen;
            var selectedOffset = bestOff;
            while (true)
            {
                // from end to beginning
                var nextMatchLength = opt[candidatePos].MLen;   // can be 1, means literal
                var nextOffset = opt[candidatePos].Off;
                opt[candidatePos].MLen = selectedMatchLength;
                opt[candidatePos].Off = selectedOffset;
                selectedMatchLength = nextMatchLength;
                selectedOffset = nextOffset;
                if (nextMatchLength > candidatePos) break;   // last match elected, first match to encode
                candidatePos -= nextMatchLength;
            }

            // encode all recorded sequences in order
            var r = 0;   // relative position (to ip)
            while (r < lastMatchPos)
            {
                var ml = opt[r].MLen;
                var offset = opt[r].Off;
                if (ml == 1) { ip++; r++; continue; }   // literal; note: can end up with several literals, in which case, skip them
                r += ml;
                if (!EncodeSequence(ref ip, ref op, ref anchor, ml, offset, limit, oend))
                    return 0;
            }
        }

        // Encode Last Literals
        var lastRunSize = (ulong)(iend - anchor);
        var llAdd = (lastRunSize + 255 - RunMask) / 255;
        var totalSize = 1 + llAdd + lastRunSize;
        if (limit && op + totalSize > oend)
            return 0;
        if (lastRunSize >= RunMask)
        {
            var accumulator = lastRunSize - RunMask;
            *op++ = RunMask << MlBits;
            for (; accumulator >= 255; accumulator -= 255) *op++ = 255;
            *op++ = (byte)accumulator;
        }
        else
        {
            *op++ = (byte)(lastRunSize << MlBits);
        }
        Buffer.MemoryCopy(anchor, op, (long)lastRunSize, (long)lastRunSize);
        op += lastRunSize;
        return (int)(op - dst);
    }
}
