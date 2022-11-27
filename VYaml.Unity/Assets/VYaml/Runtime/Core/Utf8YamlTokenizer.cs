using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using VYaml.Internal;

namespace VYaml
{
    class YamlTokenizerException : Exception
    {
        public YamlTokenizerException(in Marker marker, string message)
            : base($"{message} at {marker}")
        {
        }
    }

    struct SimpleKeyState
    {
        public bool Possible;
        public bool Required;
        public int TokenNumber;
        public Marker Start;
    }

    public ref struct Utf8YamlTokenizer
    {
        public TokenType CurrentTokenType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => currentToken.Type;
        }

        public Marker CurrentMark
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => mark;
        }

        SequenceReader<byte> reader;
        Marker mark;
        Token currentToken;

        bool streamStartProduced;
        bool streamEndProduced;
        byte currentCode;
        int indent;
        bool simpleKeyAllowed;
        int adjacentValueAllowedAt;
        int flowLevel;
        int tokensParsed;
        bool tokenAvailable;

        readonly InsertionQueue<Token> tokens;
        readonly ScalarPool scalarPool;
        readonly ExpandBuffer<SimpleKeyState> simpleKeyCandidates;
        readonly ExpandBuffer<int> indents;
        readonly ExpandBuffer<byte> lineBreaksBuffer;


        public Utf8YamlTokenizer(in ReadOnlySequence<byte> sequence)
        {
            reader = new SequenceReader<byte>(sequence);
            mark = new Marker(0, 1, 0);
            tokens = new InsertionQueue<Token>(16);
            simpleKeyCandidates = new ExpandBuffer<SimpleKeyState>(16);
            indents = new ExpandBuffer<int>(16);
            lineBreaksBuffer = new ExpandBuffer<byte>(256);
            scalarPool = new ScalarPool();

            indent = -1;
            flowLevel = 0;
            adjacentValueAllowedAt = 0;
            tokensParsed = 0;
            simpleKeyAllowed = false;
            streamStartProduced = false;
            streamEndProduced = false;
            tokenAvailable = false;

            currentToken = default;

            reader.TryPeek(out currentCode);
        }

        public void Dispose()
        {
            scalarPool.Dispose();
            simpleKeyCandidates.Dispose();
            indents.Dispose();
            lineBreaksBuffer.Dispose();
        }

        public bool Read()
        {
            if (streamEndProduced)
            {
                return false;
            }

            if (!tokenAvailable)
            {
                ConsumeMoreTokens();
            }

            if (currentToken.Content is Scalar scalar)
            {
                ReturnToPool(scalar);
            }
            currentToken = tokens.Dequeue();
            tokenAvailable = false;
            tokensParsed += 1;

            if (currentToken.Type == TokenType.StreamEnd)
            {
                streamEndProduced = true;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ReturnToPool(Scalar scalar)
        {
            scalarPool.Return(scalar);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T TakeCurrentTokenContent<T>() where T : ITokenContent
        {
            var result = currentToken;
            currentToken = default;
            return (T)result.Content!;
        }

        void ConsumeMoreTokens()
        {
            while (true)
            {
                var needMore = tokens.Count <= 0;
                if (!needMore)
                {
                    StaleSimpleKeyCandidates();
                    var span = simpleKeyCandidates.AsSpan();
                    for (var i = 0; i < span.Length; i++)
                    {
                        ref var simpleKeyState = ref span[i];
                        if (simpleKeyState.Possible && simpleKeyState.TokenNumber == tokensParsed)
                        {
                            needMore = true;
                            break;
                        }
                    }
                }
                if (!needMore)
                {
                    break;
                }
                ConsumeNextToken();
            }
            tokenAvailable = true;
        }

        void ConsumeNextToken()
        {
            if (!streamStartProduced)
            {
                ConsumeStreamStart();
                return;
            }

            SkipToNextToken();
            StaleSimpleKeyCandidates();
            UnrollIndent(mark.Col);

            if (reader.End)
            {
                ConsumeStreamEnd();
                return;
            }

            if (mark.Col == 0)
            {
                switch (currentCode)
                {
                    case (byte)'%':
                        ConsumeDirective();
                        return;
                    case (byte)'-' when reader.IsNext(YamlCodes.StreamStart) && EmptyNext(YamlCodes.StreamStart.Length):
                        ConsumeDocumentIndicator(TokenType.DocumentStart);
                        return;
                    case (byte)'.' when reader.IsNext(YamlCodes.DocStart) && EmptyNext(YamlCodes.DocStart.Length):
                        ConsumeDocumentIndicator(TokenType.DocumentEnd);
                        return;
                }
            }

            switch (currentCode)
            {
                case YamlCodes.FlowSequenceStart:
                    ConsumeFlowCollectionStart(TokenType.FlowSequenceStart);
                    break;
                case YamlCodes.FlowMapStart:
                    ConsumeFlowCollectionStart(TokenType.FlowMappingStart);
                    break;
                case YamlCodes.FlowSequenceEnd:
                    ConsumeFlowCollectionEnd(TokenType.FlowSequenceEnd);
                    break;
                case YamlCodes.FlowMapEnd:
                    ConsumeFlowCollectionEnd(TokenType.FlowMappingEnd);
                    break;
                case YamlCodes.Comma:
                    ConsumeFlowEntryStart();
                    break;
                case YamlCodes.BlockEntryIndent when !TryPeek(1, out var nextCode) ||
                                                     YamlCodes.IsEmpty(nextCode):
                    ConsumeBlockEntry();
                    break;
                case YamlCodes.ExplicitKeyIndent when !TryPeek(1, out var nextCode) ||
                                                      YamlCodes.IsEmpty(nextCode):
                    ConsumeComplexKeyStart();
                    break;
                case YamlCodes.MapValueIndent
                    when (TryPeek(1, out var nextCode) && YamlCodes.IsEmpty(nextCode)) ||
                         (flowLevel > 0 && (YamlCodes.IsAnyFlowSymbol(nextCode) || mark.Position == adjacentValueAllowedAt)):
                    ConsumeValueStart();
                    break;
                case YamlCodes.Alias:
                    ConsumeAnchor(true);
                    break;
                case YamlCodes.Anchor:
                    ConsumeAnchor(false);
                    break;
                case YamlCodes.Tag:
                    ConsumeTag();
                    break;
                case YamlCodes.BlockScalerHeader1 when flowLevel == 0:
                    ConsumeBlockScaler(true);
                    break;
                case YamlCodes.BlockScalerHeader2 when flowLevel == 0:
                    ConsumeBlockScaler(false);
                    break;
                case YamlCodes.SingleQuote:
                    ConsumeFlowScaler(true);
                    break;
                case YamlCodes.DoubleQuote:
                    ConsumeFlowScaler(false);
                    break;
                // Plain Scaler
                case YamlCodes.BlockEntryIndent when !TryPeek(1, out var nextCode) ||
                                                     YamlCodes.IsBlank(nextCode):
                    ConsumePlainScaler();
                    break;
                case YamlCodes.MapValueIndent or YamlCodes.ExplicitKeyIndent
                    when flowLevel == 0 &&
                         (!TryPeek(1, out var nextCode) || YamlCodes.IsBlank(nextCode)):
                    ConsumePlainScaler();
                    break;
                case (byte)'%' or (byte)'@' or (byte)'`':
                    throw new YamlTokenizerException(in mark, $"Unexpected character: '{currentCode}'");
                default:
                    ConsumePlainScaler();
                    break;
            }
        }

        void ConsumeStreamStart()
        {
            indent = -1;
            streamStartProduced = true;
            simpleKeyAllowed = true;
            tokens.Enqueue(new Token(TokenType.StreamStart));
            simpleKeyCandidates.Add(new SimpleKeyState());
        }

        void ConsumeStreamEnd()
        {
            // force new line
            if (mark.Col != 0)
            {
                mark.Col = 0;
                mark.Line += 1;
            }
            UnrollIndent(-1);
            RemoveSimpleKeyCandidate();
            simpleKeyAllowed = false;
            tokens.Enqueue(new Token(TokenType.StreamEnd));
        }

        void ConsumeDirective()
        {
            UnrollIndent(-1);
            RemoveSimpleKeyCandidate();
            simpleKeyAllowed = false;

            Advance(1);

            var name = scalarPool.Rent();
            try
            {
                ConsumeDirectiveName(name);
                if (name.SequenceEqual(YamlCodes.YamlDirectiveName))
                {
                    ConsumeVersionDirectiveValue();
                }
                else if (name.SequenceEqual(YamlCodes.TagDirectiveName))
                {
                    ConsumeTagDirectiveValue();
                }
                else
                {
                    // Skip current line
                    while (!reader.End && !YamlCodes.IsLineBreak(currentCode))
                    {
                        Advance(1);
                    }

                    // TODO: This should be error ?
                    tokens.Enqueue(new Token(TokenType.TagDirective));
                }
            }
            finally
            {
                scalarPool.Return(name);
            }

            while (YamlCodes.IsBlank(currentCode))
            {
                Advance(1);
            }

            if (currentCode == YamlCodes.Comment)
            {
                while (!reader.End && !YamlCodes.IsLineBreak(currentCode))
                {
                    Advance(1);
                }
            }

            if (!reader.End && !YamlCodes.IsLineBreak(currentCode))
            {
                throw new YamlTokenizerException(CurrentMark,
                    "While scanning a directive, did not find expected comment or line break");
            }

            // Eat a line break
            if (YamlCodes.IsLineBreak(currentCode))
            {
                ConsumeLineBreaks();
            }
        }

        void ConsumeDirectiveName(Scalar result)
        {
            while (YamlCodes.IsAlphaNumericDashOrUnderscore(currentCode))
            {
                result.Write(currentCode);
                Advance(1);
            }

            if (result.Length <= 0)
            {
                throw new YamlTokenizerException(CurrentMark,
                    "While scanning a directive, could not find expected directive name");
            }

            if (!reader.End && !YamlCodes.IsBlank(currentCode))
            {
                throw new YamlTokenizerException(CurrentMark,
                    "While scanning a directive, found unexpected non-alphabetical character");
            }
        }

        void ConsumeVersionDirectiveValue()
        {
            while (YamlCodes.IsBlank(currentCode))
            {
                Advance(1);
            }

            var major = ConsumeVersionDirectiveNumber();

            if (currentCode != '.')
            {
                throw new YamlTokenizerException(CurrentMark,
                    "while scanning a YAML directive, did not find expected digit or '.' character");
            }

            Advance(1);
            var minor = ConsumeVersionDirectiveNumber();
            tokens.Enqueue(new Token(TokenType.VersionDirective, new VersionDirective(major, minor)));
        }

        int ConsumeVersionDirectiveNumber()
        {
            var value = 0;
            var length = 0;
            while (YamlCodes.IsNumber(currentCode))
            {
                if (length + 1 > 9)
                {
                    throw new YamlTokenizerException(CurrentMark,
                        "While scanning a YAML directive, found exteremely long version number");
                }

                length++;
                value = value * 10 + YamlCodes.AsHex(currentCode);
                Advance(1);
            }

            if (length == 0)
            {
                throw new YamlTokenizerException(CurrentMark,
                    "While scanning a YAML directive, did not find expected version number");
            }
            return value;
        }

        void ConsumeTagDirectiveValue()
        {
            var handle = scalarPool.Rent();
            var suffix = scalarPool.Rent();
            try
            {
                // Eat whitespaces.
                while (YamlCodes.IsBlank(currentCode))
                {
                    Advance(1);
                }

                ConsumeTagHandle(true, handle);

                // Eat whitespaces
                while (YamlCodes.IsBlank(currentCode))
                {
                    Advance(1);
                }

                ConsumeTagUri(true, null, suffix);

                if (YamlCodes.IsEmpty(currentCode) || reader.End)
                {
                    tokens.Enqueue(new Token(TokenType.TagDirective, new Tag(handle.ToString(), suffix.ToString())));
                }
                else
                {
                    throw new YamlTokenizerException(CurrentMark,
                        "While scanning TAG, did not find expected whitespace or line break");
                }
            }
            finally
            {
                scalarPool.Return(handle);
                scalarPool.Return(suffix);
            }
        }

        void ConsumeDocumentIndicator(TokenType tokenType)
        {
            UnrollIndent(-1);
            RemoveSimpleKeyCandidate();
            simpleKeyAllowed = false;
            Advance(3);
            tokens.Enqueue(new Token(tokenType));
        }

        void ConsumeFlowCollectionStart(TokenType tokenType)
        {
            // The indicators '[' and '{' may start a simple key.
            SaveSimpleKeyCandidate();
            IncreaseFlowLevel();

            simpleKeyAllowed = true;

            Advance(1);
            tokens.Enqueue(new Token(tokenType));
        }

        void ConsumeFlowCollectionEnd(TokenType tokenType)
        {
            RemoveSimpleKeyCandidate();
            DecreaseFlowLevel();

            simpleKeyAllowed = false;

            Advance(1);
            tokens.Enqueue(new Token(tokenType));
        }

        void ConsumeFlowEntryStart()
        {
            RemoveSimpleKeyCandidate();
            simpleKeyAllowed = true;

            Advance(1);
            tokens.Enqueue(new Token(TokenType.FlowEntryStart));
        }

        void ConsumeBlockEntry()
        {
            if (flowLevel != 0)
            {
                throw new YamlTokenizerException(in mark, "'-' is only valid inside a block");
            }
            // Check if we are allowed to start a new entry.
            if (!simpleKeyAllowed)
            {
                throw new YamlTokenizerException(in mark, "Block sequence entries are not allowed in this context");
            }
            RollIndent(mark.Col, new Token(TokenType.BlockSequenceStart));
            RemoveSimpleKeyCandidate();
            simpleKeyAllowed = true;
            Advance(1);
            tokens.Enqueue(new Token(TokenType.BlockEntryStart));
        }

        void ConsumeComplexKeyStart()
        {
            if (flowLevel == 0)
            {
                // Check if we are allowed to start a new key (not necessarily simple).
                if (!simpleKeyAllowed)
                {
                    throw new YamlTokenizerException(in mark, "Mapping keys are not allowed in this context");
                }
                RollIndent(mark.Col, new Token(TokenType.BlockMappingStart));
            }
            RemoveSimpleKeyCandidate();

            simpleKeyAllowed = flowLevel == 0;
            Advance(1);
            tokens.Enqueue(new Token(TokenType.KeyStart));
        }

        void ConsumeValueStart()
        {
            ref var simpleKey = ref simpleKeyCandidates[^1];
            if (simpleKey.Possible)
            {
                // insert simple key
                var token = new Token(TokenType.KeyStart);
                tokens.Insert(simpleKey.TokenNumber - tokensParsed, token);

                // Add the BLOCK-MAPPING-START token if needed
                RollIndent(simpleKey.Start.Col, new Token(TokenType.BlockMappingStart), simpleKey.TokenNumber);
                ref var lastKey = ref simpleKeyCandidates[^1];
                lastKey.Possible = false;
                simpleKeyAllowed = false;
            }
            else
            {
                // The ':' indicator follows a complex key.
                if (flowLevel == 0)
                {
                    if (!simpleKeyAllowed)
                    {
                        throw new YamlTokenizerException(in mark, "Mapping values are not allowed in this context");
                    }
                    RollIndent(mark.Col, new Token(TokenType.BlockMappingStart));
                }
                simpleKeyAllowed = flowLevel == 0;
            }
            Advance(1);
            tokens.Enqueue(new Token(TokenType.ValueStart));
        }

        void ConsumeAnchor(bool alias)
        {
            SaveSimpleKeyCandidate();
            simpleKeyAllowed = false;

            var scalar = scalarPool.Rent();
            Advance(1);

            while (YamlCodes.IsAlphaNumericDashOrUnderscore(currentCode))
            {
                scalar.Write(currentCode);
                Advance(1);
            }

            if (scalar.Length <= 0)
            {
                throw new YamlTokenizerException(mark,
                    "while scanning an anchor or alias, did not find expected alphabetic or numeric character");
            }

            if (!YamlCodes.IsEmpty(currentCode) &&
                !reader.End &&
                currentCode != '?' &&
                currentCode != ':' &&
                currentCode != ',' &&
                currentCode != ']' &&
                currentCode != '}' &&
                currentCode != '%' &&
                currentCode != '@' &&
                currentCode != '`')
            {
                throw new YamlTokenizerException(in mark,
                    "while scanning an anchor or alias, did not find expected alphabetic or numeric character");
            }

            tokens.Enqueue(alias
                ? new Token(TokenType.Alias, scalar)
                : new Token(TokenType.Anchor, scalar));
        }

        void ConsumeTag()
        {
            SaveSimpleKeyCandidate();
            simpleKeyAllowed = false;

            var handle = scalarPool.Rent();
            var suffix = scalarPool.Rent();

            try
            {
                // Check if the tag is in the canonical form (verbatim).
                if (TryPeek(1, out var nextCode) && nextCode == '<')
                {
                    // Eat '!<'
                    Advance(2);
                    ConsumeTagUri(false, null, suffix);

                    if (currentCode != '>')
                    {
                        throw new YamlTokenizerException(mark, "While scanning a tag, did not find the expected '>'");
                    }

                    Advance(1);
                }
                else
                {
                    // The tag has either the '!suffix' or the '!handle!suffix'
                    ConsumeTagHandle(false, handle);

                    // Check if it is, indeed, handle.
                    var handleSpan = handle.AsSpan();
                    if (handleSpan.Length >= 2 && handleSpan[0] == '!' && handleSpan[^1] == '!')
                    {
                        ConsumeTagUri(false, null, suffix);
                    }
                    else
                    {
                        ConsumeTagUri(false, handle, suffix);
                        handle.Clear();
                        // A special case: the '!' tag.  Set the handle to '' and the
                        // suffix to '!'.
                        if (suffix.Length <= 0)
                        {
                            suffix.Clear();
                            suffix.Write((byte)'!');
                        }
                        else
                        {
                            suffix.Write((byte)'!');
                        }
                    }
                }

                if (YamlCodes.IsEmpty(currentCode) || reader.End)
                {
                    // ex 7.2, an empty scalar can follow a secondary tag
                    tokens.Enqueue(new Token(TokenType.Tag, new Tag(handle.ToString(), suffix.ToString())));
                }
                else
                {
                    throw new YamlTokenizerException(mark,
                        "While scanning a tag, did not find expected whitespace or line break");
                }
            }
            finally
            {
                scalarPool.Return(handle);
                scalarPool.Return(suffix);
            }
        }

        void ConsumeTagHandle(bool directive, Scalar buf)
        {
            if (currentCode != '!')
            {
                throw new YamlTokenizerException(mark,
                    "While scanning a tag, did not find expected '!'");
            }

            buf.Write(currentCode);
            Advance(1);

            while (YamlCodes.IsAlphaNumericDashOrUnderscore(currentCode))
            {
                buf.Write(currentCode);
                Advance(1);
            }

            // Check if the trailing character is '!' and copy it.
            if (currentCode == '!')
            {
                buf.Write(currentCode);
                Advance(1);
            }
            else if (directive)
            {
                if (!buf.SequenceEqual(stackalloc byte[] { (byte)'!' }))
                {
                    // It's either the '!' tag or not really a tag handle.  If it's a %TAG
                    // directive, it's an error.  If it's a tag token, it must be a part of
                    // URI.
                    throw new YamlTokenizerException(mark, "While parsing a tag directive, did not find expected '!'");
                }
            }
        }

        void ConsumeTagUri(bool directive, Scalar? head, Scalar uri)
        {
            // Copy the head if needed.
            // Note that we don't copy the leading '!' character.
            var length = head?.Length ?? 0;
            if (length > 1)
            {
                 uri.Write(head!.AsSpan(1, length - 1));
            }

            // The set of characters that may appear in URI is as follows:
            while (currentCode is
                       (byte)';' or (byte)'/' or (byte)'?' or (byte)':' or (byte)':' or (byte)'@' or (byte)'&' or
                       (byte)'=' or (byte)'+' or (byte)'$' or (byte)',' or (byte)'.' or (byte)'!' or (byte)'!' or
                       (byte)'~' or (byte)'*' or (byte)'\'' or (byte)'(' or (byte)')' or (byte)'[' or (byte)']' or
                       (byte)'%' ||
                   YamlCodes.IsAlphaNumericDashOrUnderscore(currentCode))
            {
                if (currentCode == '%')
                {
                    uri.WriteUnicodeCodepoint(ConsumeUriEscapes(directive));
                }
                else
                {
                    uri.Write(currentCode);
                    Advance(1);
                }

                length++;
            }
        }

        int ConsumeUriEscapes(bool directive)
        {
            var width = 0;
            var codepoint = 0;

            while (!reader.End)
            {
                TryPeek(1, out var hexcode0);
                TryPeek(2, out var hexcode1);
                if (currentCode != '%' || !YamlCodes.IsHex(hexcode0) || !YamlCodes.IsHex(hexcode1))
                {
                    throw new YamlTokenizerException(mark, "While parsing a tag, did not find URI escaped octet");
                }

                var octet = (YamlCodes.AsHex(hexcode0) << 4) + YamlCodes.AsHex(hexcode1);
                if (width == 0)
                {
                    width = octet switch {
                        _ when (octet & 0x80) == 0x00 => 1,
                        _ when (octet & 0xE0) == 0xC0 => 2,
                        _ when (octet & 0xF0) == 0xE0 => 3,
                        _ when (octet & 0xF8) == 0xF0 => 4,
                        _ => throw new YamlTokenizerException(mark,
                            "While parsing a tag, found an incorrect leading utf8 octet")
                    };
                    codepoint = octet;
                }
                else
                {
                    if ((octet & 0xc0) != 0x80)
                    {
                        throw new YamlTokenizerException(mark,
                            "While parsing a tag, found an incorrect trailing utf8 octet");
                    }
                    codepoint = (currentCode << 8) + octet;
                }

                Advance(3);

                width -= 1;
                if (width == 0)
                {
                    break;
                }
            }

            return codepoint;
        }

        void ConsumeBlockScaler(bool literal)
        {
            SaveSimpleKeyCandidate();
            simpleKeyAllowed = true;

            var chomping = 0;
            var increment = 0;
            var blockIndent = 0;

            var trailingBlank = false;
            var leadingBlank = false;
            var leadingBreak = LineBreakState.None;
            var scalar = scalarPool.Rent();

            lineBreaksBuffer.Clear();

            // skip '|' or '>'
            Advance(1);

            if (currentCode is (byte)'+' or (byte)'-')
            {
                chomping = currentCode == (byte)'+' ? 1 : -1;
                Advance(1);
                if (YamlCodes.IsNumber(currentCode))
                {
                    if (currentCode == (byte)'0')
                    {
                        throw new YamlTokenizerException(in mark,
                            "While scanning a block scalar, found an indentation indicator equal to 0");
                    }

                    increment = YamlCodes.AsHex(currentCode);
                    Advance(1);
                }
            }
            else if (YamlCodes.IsNumber(currentCode))
            {
                if (currentCode == (byte)'0')
                {
                    throw new YamlTokenizerException(in mark,
                        "While scanning a block scalar, found an indentation indicator equal to 0");
                }
                increment = YamlCodes.AsHex(currentCode);
                Advance(1);

                if (currentCode is (byte)'+' or (byte)'-')
                {
                    chomping = currentCode == (byte)'+' ? 1 : -1;
                    Advance(1);
                }
            }

            // Eat whitespaces and comments to the end of the line.
            while (YamlCodes.IsBlank(currentCode))
            {
                Advance(1);
            }

            if (currentCode == YamlCodes.Comment)
            {
                while (!reader.End && !YamlCodes.IsLineBreak(currentCode))
                {
                    Advance(1);
                }
            }

            // Check if we are at the end of the line.
            if (!reader.End && !YamlCodes.IsLineBreak(currentCode))
            {
                throw new YamlTokenizerException(in mark,
                    "While scanning a block scalar, did not find expected commnet or line break");
            }

            if (YamlCodes.IsLineBreak(currentCode))
            {
                ConsumeLineBreaks();
            }

            if (increment > 0)
            {
                blockIndent = indent >= 0 ? indent + increment : increment;
            }

            // Scan the leading line breaks and determine the indentation level if needed.
            ConsumeBlockScalarBreaks(ref blockIndent, lineBreaksBuffer);

            while (mark.Col == blockIndent)
            {
                // We are at the beginning of a non-empty line.
                trailingBlank = YamlCodes.IsBlank(currentCode);
                if (!literal &&
                    leadingBreak != LineBreakState.None &&
                    !leadingBlank &&
                    !trailingBlank)
                {
                    if (lineBreaksBuffer.Length <= 0)
                    {
                        scalar.Write(YamlCodes.Space);
                    }
                }
                else
                {
                    scalar.Write(leadingBreak);
                }

                scalar.Write(lineBreaksBuffer.AsSpan());
                leadingBlank = YamlCodes.IsBlank(currentCode);
                leadingBreak = LineBreakState.None;
                lineBreaksBuffer.Clear();

                while (!reader.End && !YamlCodes.IsLineBreak(currentCode))
                {
                    scalar.Write(currentCode);
                    Advance(1);
                }
                // break on EOF
                if (reader.End) break;

                leadingBreak = ConsumeLineBreaks();
                // Eat the following indentation spaces and line breaks.
                ConsumeBlockScalarBreaks(ref blockIndent, lineBreaksBuffer);
            }

            // Chomp the tail.
            if (chomping != -1)
            {
                scalar.Write(leadingBreak);
            }
            if (chomping == 1)
            {
                scalar.Write(lineBreaksBuffer.AsSpan());
            }

            var tokenType = literal ? TokenType.LiteralScalar : TokenType.FoldedScalar;
            tokens.Enqueue(new Token(tokenType, scalar));
        }

        void ConsumeBlockScalarBreaks(ref int blockIndent, ExpandBuffer<byte> blockLineBreaks)
        {
            var maxIndent = 0;
            while (true)
            {
                while ((blockIndent == 0 || mark.Col < blockIndent) &&
                       currentCode == YamlCodes.Space)
                {
                    Advance(1);
                }

                if (mark.Col > maxIndent)
                {
                    maxIndent = mark.Col;
                }

                // Check for a tab character messing the indentation.
                if ((blockIndent == 0 || mark.Col < blockIndent) && currentCode == YamlCodes.Tab)
                {
                    throw new YamlTokenizerException(in mark,
                        "while scanning a block scalar, found a tab character where an indentation space is expected");
                }

                if (!YamlCodes.IsLineBreak(currentCode))
                {
                    break;
                }

                switch (ConsumeLineBreaks())
                {
                    case LineBreakState.Lf:
                        blockLineBreaks.Add(YamlCodes.Lf);
                        break;
                    case LineBreakState.CrLf:
                        blockLineBreaks.Add(YamlCodes.Cr);
                        blockLineBreaks.Add(YamlCodes.Lf);
                        break;
                    case LineBreakState.Cr:
                        blockLineBreaks.Add(YamlCodes.Cr);
                        break;
                }
            }

            if (blockIndent == 0)
            {
                blockIndent = maxIndent;
                if (blockIndent < indent + 1)
                {
                    blockIndent = indent + 1;
                }
                else if (blockIndent < 1)
                {
                    blockIndent = 1;
                }
            }
        }

        void ConsumeFlowScaler(bool singleQuote)
        {
            SaveSimpleKeyCandidate();
            simpleKeyAllowed = false;

            var leadingBreak = default(LineBreakState);
            var trailingBreak = default(LineBreakState);
            var isLeadingBlanks = false;
            var scalar = scalarPool.Rent();

            Span<byte> whitespaceBuffer = stackalloc byte[128];
            var whitespaceLength = 0;

            // Eat the left quote
            Advance(1);

            while (true)
            {
                if (mark.Col == 0 &&
                    (reader.IsNext(YamlCodes.StreamStart) ||
                     reader.IsNext(YamlCodes.DocStart)) &&
                    !TryPeek(3, out _))
                {
                    throw new YamlTokenizerException(mark,
                        "while scanning a quoted scalar, found unexpected document indicator");
                }

                if (reader.End)
                {
                    throw new YamlTokenizerException(mark,
                        "while scanning a quoted scalar, found unexpected end of stream");
                }

                isLeadingBlanks = false;

                // Consume non-blank characters
                while (!reader.End && !YamlCodes.IsEmpty(currentCode))
                {
                    switch (currentCode)
                    {
                        // Check for an escaped single quote
                        case YamlCodes.SingleQuote when TryPeek(1, out var nextCode) &&
                                                        nextCode == YamlCodes.SingleQuote && singleQuote:
                            scalar.Write((byte)'\'');
                            Advance(2);
                            break;
                        // Check for the right quote.
                        case YamlCodes.SingleQuote when singleQuote:
                        case YamlCodes.DoubleQuote when !singleQuote:
                            goto LOOPEND;
                        // Check for an escaped line break.
                        case (byte)'\\' when !singleQuote &&
                                             TryPeek(1, out var nextCode) &&
                                             YamlCodes.IsLineBreak(nextCode):
                            Advance(1);
                            ConsumeLineBreaks();
                            isLeadingBlanks = true;
                            break;
                        // Check for an escape sequence.
                        case (byte)'\\' when !singleQuote:
                            var codeLength = 0;
                            TryPeek(1, out var escaped);
                            switch (escaped)
                            {
                                case (byte)'0':
                                    scalar.Write((byte)'\0');
                                    break;
                                case (byte)'a':
                                    scalar.Write((byte)'\a');
                                    break;
                                case (byte)'b':
                                    scalar.Write((byte)'\b');
                                    break;
                                case (byte)'t':
                                    scalar.Write((byte)'\t');
                                    break;
                                case (byte)'n':
                                    scalar.Write((byte)'\n');
                                    break;
                                case (byte)'v':
                                    scalar.Write((byte)'\v');
                                    break;
                                case (byte)'f':
                                    scalar.Write((byte)'\f');
                                    break;
                                case (byte)'r':
                                    scalar.Write((byte)'\r');
                                    break;
                                case (byte)'e':
                                    scalar.Write(0x1b);
                                    break;
                                case (byte)' ':
                                    scalar.Write((byte)' ');
                                    break;
                                case (byte)'"':
                                    scalar.Write((byte)'"');
                                    break;
                                case (byte)'\'':
                                    scalar.Write((byte)'\'');
                                    break;
                                case (byte)'\\':
                                    scalar.Write((byte)'\\');
                                    break;
                                // NEL (#x85)
                                case (byte)'N':
                                    scalar.WriteUnicodeCodepoint(0x85);
                                    break;
                                // #xA0
                                case (byte)'_':
                                    scalar.WriteUnicodeCodepoint(0xA0);
                                    break;
                                // LS (#x2028)
                                case (byte)'L':
                                    scalar.WriteUnicodeCodepoint(0x2028);
                                    break;
                                // PS (#x2029)
                                case (byte)'P':
                                    scalar.WriteUnicodeCodepoint(0x2029);
                                    break;
                                case (byte)'x':
                                    codeLength = 2;
                                    break;
                                case (byte)'u':
                                    codeLength = 4;
                                    break;
                                case (byte)'U':
                                    codeLength = 8;
                                    break;
                                default:
                                    throw new YamlTokenizerException(mark,
                                        "while parsing a quoted scalar, found unknown escape character");
                            }

                            Advance(2);
                            // Consume an arbitrary escape code.
                            if (codeLength > 0)
                            {
                                var codepoint = 0;
                                for (var i = 0; i < codeLength; i++)
                                {
                                    if (TryPeek(i, out var hex) && YamlCodes.IsHex(hex))
                                    {
                                        codepoint = (codepoint << 4) + YamlCodes.AsHex(hex);
                                    }
                                    else
                                    {
                                        throw new YamlTokenizerException(mark,
                                            "While parsing a quoted scalar, did not find expected hexadecimal number");
                                    }
                                }
                                scalar.WriteUnicodeCodepoint(codepoint);
                            }

                            Advance(codeLength);
                            break;
                        default:
                            scalar.Write(currentCode);
                            Advance(1);
                            break;
                    }
                }

                // Consume blank characters.
                while (YamlCodes.IsBlank(currentCode) || YamlCodes.IsLineBreak(currentCode))
                {
                    if (YamlCodes.IsBlank(currentCode))
                    {
                        // Consume a space or a tab character.
                        if (!isLeadingBlanks)
                        {
                            if (whitespaceBuffer.Length <= whitespaceLength)
                            {
                                whitespaceBuffer = new byte[whitespaceBuffer.Length * 2];
                            }
                            whitespaceBuffer[whitespaceLength++] = currentCode;
                        }
                        Advance(1);
                    }
                    else
                    {
                        // Check if it is a first line break.
                        if (isLeadingBlanks)
                        {
                            trailingBreak = ConsumeLineBreaks();
                        }
                        else
                        {
                            whitespaceBuffer.Clear();
                            leadingBreak = ConsumeLineBreaks();
                            isLeadingBlanks = true;
                        }
                    }
                }

                // Join the whitespaces or fold line breaks.
                if (isLeadingBlanks)
                {
                    if (leadingBreak == LineBreakState.None)
                    {
                        scalar.Write(trailingBreak);
                        trailingBreak = LineBreakState.None;
                    }
                    else
                    {
                        if (trailingBreak == LineBreakState.None)
                        {
                            scalar.Write(YamlCodes.Space);
                        }
                        else
                        {
                            scalar.Write(trailingBreak);
                            trailingBreak = LineBreakState.None;
                        }
                        leadingBreak = LineBreakState.None;
                    }
                }
                else
                {
                    scalar.Write(whitespaceBuffer[..whitespaceLength]);
                    whitespaceLength = 0;
                }
            }

            // Eat the right quote
            LOOPEND:
            Advance(1);
            simpleKeyAllowed = isLeadingBlanks;

            // From spec: To ensure JSON compatibility, if a key inside a flow mapping is JSON-like,
            // YAML allows the following value to be specified adjacent to the “:”.
            adjacentValueAllowedAt = mark.Position;

            tokens.Enqueue(new Token(singleQuote
                ? TokenType.SingleQuotedScaler
                : TokenType.DoubleQuotedScaler,
                scalar));
        }

        void ConsumePlainScaler()
        {
            SaveSimpleKeyCandidate();
            simpleKeyAllowed = false;

            var currentIndent = indent + 1;
            var leadingBreak = default(LineBreakState);
            var trailingBreak = default(LineBreakState);
            var isLeadingBlanks = false;
            var scalar = scalarPool.Rent();

            Span<byte> whitespaceBuffer = stackalloc byte[128];
            var whitespaceLength = 0;

            while (true)
            {
                // Check for a document indicator
                if (mark.Col == 0 &&
                    (reader.IsNext(YamlCodes.StreamStart) || reader.IsNext(YamlCodes.DocStart)) &&
                    EmptyNext(3))
                {
                    break;
                }

                if (currentCode == YamlCodes.Comment)
                {
                    break;
                }

                while (!reader.End && !YamlCodes.IsEmpty(currentCode))
                {
                    if (currentCode == YamlCodes.MapValueIndent)
                    {
                        var hasNext = TryPeek(1, out var nextCode);
                        if (!hasNext ||
                            YamlCodes.IsEmpty(nextCode) ||
                            (flowLevel > 0 && YamlCodes.IsAnyFlowSymbol(nextCode)))
                        {
                            break;
                        }
                    }
                    else if (YamlCodes.IsAnyFlowSymbol(currentCode) && flowLevel > 0)
                    {
                        break;
                    }

                    if (isLeadingBlanks || whitespaceLength > 0)
                    {
                        if (isLeadingBlanks)
                        {
                            if (leadingBreak == LineBreakState.None)
                            {
                                scalar.Write(trailingBreak);
                                trailingBreak = LineBreakState.None;
                            }
                            else
                            {
                                if (trailingBreak == LineBreakState.None)
                                {
                                    scalar.Write(YamlCodes.Space);
                                }
                                else
                                {
                                    scalar.Write(trailingBreak);
                                    trailingBreak = LineBreakState.None;
                                }
                                leadingBreak = LineBreakState.None;
                            }
                            isLeadingBlanks = false;
                        }
                        else
                        {
                            scalar.Write(whitespaceBuffer[..whitespaceLength]);
                            whitespaceLength = 0;
                        }
                    }

                    scalar.Write(currentCode);
                    Advance(1);
                }

                // is the end?
                if (!YamlCodes.IsEmpty(currentCode))
                {
                    break;
                }

                // whitespaces or line-breaks
                while (YamlCodes.IsEmpty(currentCode))
                {
                    // whitespaces
                    if (YamlCodes.IsBlank(currentCode))
                    {
                        if (isLeadingBlanks && mark.Col < currentIndent && currentCode == YamlCodes.Tab)
                        {
                            throw new YamlTokenizerException(mark, "While scanning a plain scaler, found a tab");
                        }
                        if (!isLeadingBlanks)
                        {
                            // If the buffer on the stack is insufficient, it is decompressed.
                            // This is probably a very rare case.
                            if (whitespaceLength >= whitespaceBuffer.Length)
                            {
                                whitespaceBuffer = new byte[whitespaceBuffer.Length * 2];
                            }
                            whitespaceBuffer[whitespaceLength++] = currentCode;
                        }
                        Advance(1);
                    }
                    // line-break
                    else
                    {
                        // Check if it is a first line break
                        if (isLeadingBlanks)
                        {
                            trailingBreak = ConsumeLineBreaks();
                        }
                        else
                        {
                            leadingBreak = ConsumeLineBreaks();
                            isLeadingBlanks = true;
                            whitespaceLength = 0;
                        }
                    }
                }

                // check indentation level
                if (flowLevel == 0 && mark.Col < currentIndent)
                {
                    break;
                }
            }

            simpleKeyAllowed = isLeadingBlanks;
            tokens.Enqueue(new Token(TokenType.PlainScalar, scalar));
        }

        void SkipToNextToken()
        {
            while (true)
            {
                switch (currentCode)
                {
                    case YamlCodes.Space:
                        Advance(1);
                        break;
                    case YamlCodes.Tab when flowLevel > 0 || !simpleKeyAllowed:
                        Advance(1);
                        break;
                    case YamlCodes.Cr:
                    case YamlCodes.Lf:
                        ConsumeLineBreaks();
                        if (flowLevel == 0) simpleKeyAllowed = true;
                        break;
                    case YamlCodes.Comment:
                        while (!reader.End && !YamlCodes.IsLineBreak(currentCode))
                        {
                            Advance(1);
                        }
                        break;
                    case 0xFE when reader.IsNext(YamlCodes.Bom):
                        Advance(YamlCodes.Bom.Length);
                        break;
                    default:
                        return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Advance(int offset)
        {
            for (var i = 0; i < offset; i++)
            {
                mark.Position += 1;
                if (currentCode == YamlCodes.Lf)
                {
                    mark.Line += 1;
                    mark.Col = 0;
                }
                else
                {
                    mark.Col += 1;
                }
                reader.Advance(1);
                reader.TryPeek(out currentCode);
            }
        }

        LineBreakState ConsumeLineBreaks()
        {
            if (reader.End)
                return LineBreakState.None;

            switch (currentCode)
            {
                case YamlCodes.Cr:
                    if (TryPeek(1, out var secondCode) && secondCode == YamlCodes.Lf)
                    {
                        Advance(2);
                        return LineBreakState.CrLf;
                    }
                    Advance(1);
                    return LineBreakState.Cr;
                case YamlCodes.Lf:
                    Advance(1);
                    return LineBreakState.Lf;
            }
            return LineBreakState.None;
        }

        void StaleSimpleKeyCandidates()
        {
            for (var i = 0; i < simpleKeyCandidates.Length; i++)
            {
                ref var simpleKey = ref simpleKeyCandidates[i];
                if (simpleKey.Possible &&
                    (simpleKey.Start.Line < mark.Line || simpleKey.Start.Position + 1024 < mark.Position))
                {
                    if (simpleKey.Required)
                    {
                        throw new YamlTokenizerException(mark, "Simple key expect ':'");
                    }
                    simpleKey.Possible = false;
                }
            }
        }

        void SaveSimpleKeyCandidate()
        {
            if (!simpleKeyAllowed)
            {
                return;
            }

            ref var last = ref simpleKeyCandidates[^1];
            if (last.Possible && last.Required)
            {
                throw new YamlTokenizerException(mark, "Simple key expected");
            }

            simpleKeyCandidates[^1] = new SimpleKeyState
            {
                Start = mark,
                Possible = true,
                Required = flowLevel > 0 && indent == mark.Col,
                TokenNumber = tokensParsed + tokens.Count
            };
        }

        void RemoveSimpleKeyCandidate()
        {
            ref var last = ref simpleKeyCandidates[^1];
            if (last.Possible && last.Required)
            {
                throw new YamlTokenizerException(mark, "Simple key expected");
            }
            last.Possible = false;
        }

        void RollIndent(int colTo, in Token nextToken, int insertNumber = -1)
        {
            if (flowLevel > 0 || indent >= colTo)
            {
                return;
            }

            indents.Add(indent);
            indent = colTo;
            if (insertNumber >= 0)
            {
                tokens.Insert(insertNumber - tokensParsed, nextToken);
            }
            else
            {
                tokens.Enqueue(nextToken);
            }
        }

        void UnrollIndent(int col)
        {
            if (flowLevel > 0)
            {
                return;
            }
            while (indent > col)
            {
                tokens.Enqueue(new Token(TokenType.BlockEnd));
                indent = indents.Pop();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void IncreaseFlowLevel()
        {
            simpleKeyCandidates.Add(new SimpleKeyState());
            flowLevel++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void DecreaseFlowLevel()
        {
            if (flowLevel <= 0) return;
            flowLevel--;
            simpleKeyCandidates.Pop();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly bool EmptyNext(int offset)
        {
            if (reader.End || reader.Remaining <= offset)
                return true;

            // If offset doesn't fall inside current segment move to next until we find correct one
            if (reader.CurrentSpanIndex + offset <= reader.CurrentSpan.Length - 1)
            {
                var nextCode = reader.CurrentSpan[reader.CurrentSpanIndex + offset];
                return YamlCodes.IsEmpty(nextCode);
            }

            var remainingOffset = offset - (reader.CurrentSpan.Length - reader.CurrentSpanIndex) - 1;
            var nextPosition = reader.Position;
            ReadOnlyMemory<byte> currentMemory;

            while (reader.Sequence.TryGet(ref nextPosition, out currentMemory, advance: true))
            {
                // Skip empty segment
                if (currentMemory.Length > 0)
                {
                    if (remainingOffset >= currentMemory.Length)
                    {
                        // Subtract current non consumed data
                        remainingOffset -= currentMemory.Length;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            return YamlCodes.IsEmpty(currentMemory.Span[remainingOffset]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly bool TryPeek(long offset, out byte value)
        {
            // If we've got data and offset is not out of bounds
            if (reader.End || reader.Remaining <= offset)
            {
                value = default;
                return false;
            }

            // If offset doesn't fall inside current segment move to next until we find correct one
            if (reader.CurrentSpanIndex + offset <= reader.CurrentSpan.Length - 1)
            {
                value = reader.CurrentSpan[reader.CurrentSpanIndex + (int)offset];
                return true;
            }

            var remainingOffset = offset - (reader.CurrentSpan.Length - reader.CurrentSpanIndex) - 1;
            var nextPosition = reader.Position;
            ReadOnlyMemory<byte> currentMemory;

            while (reader.Sequence.TryGet(ref nextPosition, out currentMemory, advance: true))
            {
                // Skip empty segment
                if (currentMemory.Length > 0)
                {
                    if (remainingOffset >= currentMemory.Length)
                    {
                        // Subtract current non consumed data
                        remainingOffset -= currentMemory.Length;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            value = currentMemory.Span[(int)remainingOffset];
            return true;
        }
    }
}