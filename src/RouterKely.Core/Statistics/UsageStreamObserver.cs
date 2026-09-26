using System.Runtime.CompilerServices;

namespace RouterKely.Core.Statistics;

public struct UsageStreamObserver
{
    private const int PropertyCapacity = 32;
    private PropertyBuffer _property;
    private int _depth;
    private int _usageDepth;
    private int _propertyLength;
    private bool _inString;
    private bool _escaped;
    private bool _captureProperty;
    private bool _invalidProperty;
    private bool _inUsage;
    private bool _awaitingColon;
    private bool _awaitingValue;
    private bool _readingNumber;
    private bool _numberOverflow;
    private TokenField _pendingField;
    private TokenField _numberField;
    private long _number;
    private long _inputTokens;
    private long _cachedInputTokens;
    private long _outputTokens;
    private bool _found;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
            ReadByte(value);
    }

    public UsageObservation Read()
    {
        if (_readingNumber)
            CompleteNumber();

        return new UsageObservation(
            _inputTokens,
            Math.Min(_cachedInputTokens, _inputTokens),
            _outputTokens,
            _found);
    }

    private void ReadByte(byte value)
    {
        if (_readingNumber)
        {
            if (value is >= (byte)'0' and <= (byte)'9')
            {
                int digit = value - (byte)'0';
                if (_number > (long.MaxValue - digit) / 10)
                    _numberOverflow = true;
                else
                    _number = (_number * 10) + digit;
                return;
            }

            CompleteNumber();
        }

        if (_inString)
        {
            ReadStringByte(value);
            return;
        }

        if (_awaitingColon)
        {
            if (IsWhitespace(value))
                return;
            if (value == (byte)':')
            {
                _awaitingColon = false;
                _awaitingValue = true;
                return;
            }

            ResetPendingField();
        }

        if (_awaitingValue)
        {
            if (IsWhitespace(value))
                return;

            TokenField field = _pendingField;
            ResetPendingField();
            if (field == TokenField.Usage && value == (byte)'{')
            {
                _depth++;
                _inUsage = true;
                _usageDepth = _depth;
                return;
            }

            if (field is TokenField.Input or TokenField.Cached or TokenField.Output &&
                value is >= (byte)'0' and <= (byte)'9')
            {
                _readingNumber = true;
                _numberField = field;
                _number = value - (byte)'0';
                _numberOverflow = false;
                return;
            }
        }

        switch (value)
        {
            case (byte)'"':
                _inString = true;
                _escaped = false;
                _propertyLength = 0;
                _invalidProperty = false;
                _captureProperty = (!_inUsage && _depth == 1) || (_inUsage && _depth >= _usageDepth);
                break;
            case (byte)'{':
            case (byte)'[':
                _depth++;
                break;
            case (byte)'}':
            case (byte)']':
                if (_inUsage && _depth == _usageDepth)
                    _inUsage = false;
                if (_depth > 0)
                    _depth--;
                break;
        }
    }

    private void ReadStringByte(byte value)
    {
        if (_escaped)
        {
            _escaped = false;
            _invalidProperty = true;
            return;
        }

        if (value == (byte)'\\')
        {
            _escaped = true;
            return;
        }

        if (value != (byte)'"')
        {
            if (_captureProperty)
            {
                if (_propertyLength < PropertyCapacity)
                    _property[_propertyLength++] = value;
                else
                    _invalidProperty = true;
            }
            return;
        }

        _inString = false;
        if (!_captureProperty || _invalidProperty)
            return;

        ReadOnlySpan<byte> property = _property;
        TokenField field = IdentifyProperty(property[.._propertyLength]);
        if (field == TokenField.None || (!_inUsage && field != TokenField.Usage))
            return;

        _pendingField = field;
        _awaitingColon = true;
    }

    private void CompleteNumber()
    {
        if (!_numberOverflow)
        {
            switch (_numberField)
            {
                case TokenField.Input:
                    _inputTokens = _number;
                    _found = true;
                    break;
                case TokenField.Cached:
                    _cachedInputTokens = _number;
                    break;
                case TokenField.Output:
                    _outputTokens = _number;
                    _found = true;
                    break;
            }
        }

        _readingNumber = false;
        _numberField = TokenField.None;
        _number = 0;
        _numberOverflow = false;
    }

    private void ResetPendingField()
    {
        _awaitingColon = false;
        _awaitingValue = false;
        _pendingField = TokenField.None;
    }

    private static TokenField IdentifyProperty(ReadOnlySpan<byte> property)
    {
        if (property.SequenceEqual("usage"u8))
            return TokenField.Usage;
        if (property.SequenceEqual("prompt_tokens"u8) || property.SequenceEqual("input_tokens"u8))
            return TokenField.Input;
        if (property.SequenceEqual("completion_tokens"u8) || property.SequenceEqual("output_tokens"u8))
            return TokenField.Output;
        if (property.SequenceEqual("prompt_cache_hit_tokens"u8) || property.SequenceEqual("cached_tokens"u8))
            return TokenField.Cached;
        return TokenField.None;
    }

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private enum TokenField
    {
        None,
        Usage,
        Input,
        Cached,
        Output
    }

    [InlineArray(PropertyCapacity)]
    private struct PropertyBuffer
    {
        private byte _element0;
    }
}
