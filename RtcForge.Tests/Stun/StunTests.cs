using RtcForge.Stun;

namespace RtcForge.Tests.Stun;

public class StunTests
{
    [Fact]
    public void TryParse_SimpleBindingRequest_ReturnsTrue()
    {
        // Arrange
        byte[] buffer = new byte[20];
        buffer[1] = 0x01; // Type: BindingRequest (0x0001)
        // Length: 0
        buffer[4] = 0x21; buffer[5] = 0x12; buffer[6] = 0xA4; buffer[7] = 0x42; // Magic Cookie
        for (int i = 8; i < 20; i++)
        {
            buffer[i] = (byte)i; // Transaction ID
        }

        // Act
        bool result = StunMessage.TryParse(buffer, out StunMessage message);

        // Assert
        Assert.True(result);
        Assert.Equal(StunMessageType.BindingRequest, message.Type);
        Assert.Equal(8, message.TransactionId[0]);
    }

    [Fact]
    public void Serialize_BindingRequest_ProducesCorrectBytes()
    {
        // Arrange
        var message = new StunMessage
        {
            Type = StunMessageType.BindingRequest,
            TransactionId = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]
        };
        byte[] buffer = new byte[20];

        // Act
        int length = message.Serialize(buffer);

        // Assert
        Assert.Equal(20, length);
        Assert.Equal(0x00, buffer[0]);
        Assert.Equal(0x01, buffer[1]);
        Assert.Equal(0x21, buffer[4]);
        Assert.Equal(1, buffer[8]);
    }

    [Fact]
    public void TryParse_WithAttributes_ReturnsAttributes()
    {
        // Arrange
        byte[] buffer = new byte[28];
        buffer[1] = 0x01; // Type: BindingRequest
        buffer[3] = 0x08; // Length: 8
        buffer[4] = 0x21; buffer[5] = 0x12; buffer[6] = 0xA4; buffer[7] = 0x42;

        // Attribute: Username (0x0006), Length: 4, Value: "test"
        buffer[20] = 0x00; buffer[21] = 0x06;
        buffer[22] = 0x00; buffer[23] = 0x04;
        buffer[24] = (byte)'t'; buffer[25] = (byte)'e'; buffer[26] = (byte)'s'; buffer[27] = (byte)'t';

        // Act
        bool result = StunMessage.TryParse(buffer, out StunMessage message);

        // Assert
        Assert.True(result);
        Assert.Single(message.Attributes);
        Assert.Equal(StunAttributeType.Username, message.Attributes[0].Type);
        Assert.Equal("test", System.Text.Encoding.UTF8.GetString(message.Attributes[0].Value));
    }

    [Fact]
    public void Serialize_WithIntegrityAndFingerprint_ValidatesSuccessfully()
    {
        var key = System.Text.Encoding.UTF8.GetBytes("test-password");
        var message = new StunMessage
        {
            Type = StunMessageType.BindingRequest,
            TransactionId = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12],
            Attributes =
            {
                new StunAttribute { Type = StunAttributeType.Username, Value = System.Text.Encoding.UTF8.GetBytes("local:remote") },
                new StunAttribute { Type = StunAttributeType.MessageIntegrity },
                new StunAttribute { Type = StunAttributeType.Fingerprint }
            }
        };

        byte[] buffer = new byte[message.GetSerializedLength()];
        int length = message.Serialize(buffer, key);

        Assert.True(StunMessage.TryParse(buffer.AsSpan(0, length), out var parsed));
        Assert.NotNull(parsed.RawBytes);
        Assert.True(StunSecurity.ValidateMessageIntegrity(parsed.RawBytes!, key));
        Assert.True(StunSecurity.ValidateFingerprint(parsed.RawBytes!));
    }

    [Fact]
    public void TryParse_InvalidHeaderFields_ReturnFalse()
    {
        byte[] buffer = new byte[20];
        buffer[4] = 0x21; buffer[5] = 0x12; buffer[6] = 0xA4; buffer[7] = 0x42;

        Assert.False(StunMessage.TryParse(buffer.AsSpan(0, 19), out _));

        buffer[0] = 0xC0;
        Assert.False(StunMessage.TryParse(buffer, out _));

        buffer[0] = 0;
        buffer[2] = 0; buffer[3] = 4;
        Assert.False(StunMessage.TryParse(buffer, out _));

        buffer[2] = 0; buffer[3] = 0;
        buffer[4] = 0;
        Assert.False(StunMessage.TryParse(buffer, out _));
    }

    [Fact]
    public void TryParse_TruncatedAttributeStopsParsingButKeepsMessage()
    {
        byte[] buffer = new byte[24];
        buffer[1] = 0x01;
        buffer[3] = 0x04;
        buffer[4] = 0x21; buffer[5] = 0x12; buffer[6] = 0xA4; buffer[7] = 0x42;
        buffer[20] = 0x00; buffer[21] = 0x06;
        buffer[22] = 0x00; buffer[23] = 0x08;

        Assert.True(StunMessage.TryParse(buffer, out var message));
        Assert.Empty(message.Attributes);
    }

    [Fact]
    public void Serialize_WithTooSmallBuffer_ReturnsMinusOne()
    {
        var message = new StunMessage
        {
            Type = StunMessageType.BindingRequest,
            TransactionId = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12],
            Attributes = { new StunAttribute { Type = StunAttributeType.Username, Value = System.Text.Encoding.UTF8.GetBytes("test") } }
        };

        Assert.Equal(-1, message.Serialize(new byte[message.GetSerializedLength() - 1]));
    }

    [Fact]
    public void Serialize_MessageIntegrityWithoutKey_Throws()
    {
        var message = new StunMessage
        {
            Type = StunMessageType.BindingRequest,
            TransactionId = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12],
            Attributes = { new StunAttribute { Type = StunAttributeType.MessageIntegrity } }
        };

        Assert.Throws<ArgumentNullException>(() => message.Serialize(new byte[message.GetSerializedLength()]));
    }

    [Fact]
    public void AttributeHelpers_InvalidTypeOrLength_ReturnNull()
    {
        Assert.Null(new StunAttribute { Type = StunAttributeType.Username, Value = [0, 1, 2, 3, 4, 5, 6, 7] }.GetXorMappedAddress(new byte[12]));
        Assert.Null(new StunAttribute { Type = StunAttributeType.XorMappedAddress, Value = [0, 1, 2, 3] }.GetXorMappedAddress(new byte[12]));
        Assert.Null(new StunAttribute { Type = StunAttributeType.Username, Value = [0, 0, 4, 4] }.GetErrorCode());
        Assert.Null(new StunAttribute { Type = StunAttributeType.ErrorCode, Value = [0, 0, 4] }.GetErrorCode());
        Assert.Null(new StunAttribute { Value = [1, 2, 3] }.GetUInt32());
    }
}
