using System.Text;
using RtcForge.Stun;

namespace RtcForge.Tests.Stun;

public class StunSecurityTests
{
    [Fact]
    public void CalculateFingerprint_DeterministicForSameInput()
    {
        byte[] data = Encoding.UTF8.GetBytes("hello world");

        uint fp1 = StunSecurity.CalculateFingerprint(data);
        uint fp2 = StunSecurity.CalculateFingerprint(data);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void CalculateFingerprint_DifferentInputs_DifferentResults()
    {
        uint fp1 = StunSecurity.CalculateFingerprint(Encoding.UTF8.GetBytes("hello"));
        uint fp2 = StunSecurity.CalculateFingerprint(Encoding.UTF8.GetBytes("world"));

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void CalculateMessageIntegrity_DeterministicForSameInput()
    {
        byte[] data = Encoding.UTF8.GetBytes("test data");
        byte[] key = Encoding.UTF8.GetBytes("secret");

        byte[] hmac1 = StunSecurity.CalculateMessageIntegrity(data, key);
        byte[] hmac2 = StunSecurity.CalculateMessageIntegrity(data, key);

        Assert.Equal(hmac1, hmac2);
    }

    [Fact]
    public void CalculateMessageIntegrity_DifferentKeys_DifferentResults()
    {
        byte[] data = Encoding.UTF8.GetBytes("test data");

        byte[] hmac1 = StunSecurity.CalculateMessageIntegrity(data, Encoding.UTF8.GetBytes("key1"));
        byte[] hmac2 = StunSecurity.CalculateMessageIntegrity(data, Encoding.UTF8.GetBytes("key2"));

        Assert.NotEqual(hmac1, hmac2);
    }

    [Fact]
    public void CalculateMessageIntegrity_ProducesSha1Length()
    {
        byte[] data = Encoding.UTF8.GetBytes("test");
        byte[] key = Encoding.UTF8.GetBytes("key");

        byte[] hmac = StunSecurity.CalculateMessageIntegrity(data, key);

        Assert.Equal(20, hmac.Length); // SHA-1 = 20 bytes
    }

    [Fact]
    public void DeriveTurnKey_ProducesMd5Hash()
    {
        byte[] key = StunSecurity.DeriveTurnKey("user", "realm", "pass");

        Assert.Equal(16, key.Length); // MD5 = 16 bytes
    }

    [Fact]
    public void DeriveTurnKey_DeterministicForSameInput()
    {
        byte[] key1 = StunSecurity.DeriveTurnKey("user", "realm", "pass");
        byte[] key2 = StunSecurity.DeriveTurnKey("user", "realm", "pass");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void DeriveTurnKey_DifferentInputs_DifferentResults()
    {
        byte[] key1 = StunSecurity.DeriveTurnKey("user1", "realm", "pass");
        byte[] key2 = StunSecurity.DeriveTurnKey("user2", "realm", "pass");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void ValidateFingerprint_NoFingerprintAttribute_ReturnsFalse()
    {
        // Minimal STUN message with no fingerprint
        byte[] buffer = new byte[20];
        buffer[1] = 0x01; // BindingRequest
        buffer[4] = 0x21; buffer[5] = 0x12; buffer[6] = 0xA4; buffer[7] = 0x42; // magic cookie

        bool result = StunSecurity.ValidateFingerprint(buffer);

        Assert.False(result);
    }

    [Fact]
    public void ValidateMessageIntegrity_EmptyKey_ReturnsFalse()
    {
        byte[] buffer = new byte[20];
        buffer[1] = 0x01;
        buffer[4] = 0x21; buffer[5] = 0x12; buffer[6] = 0xA4; buffer[7] = 0x42;

        bool result = StunSecurity.ValidateMessageIntegrity(buffer, []);

        Assert.False(result);
    }

    [Fact]
    public void ValidateMessageIntegrity_NoIntegrityAttribute_ReturnsFalse()
    {
        byte[] key = Encoding.UTF8.GetBytes("test-key");
        byte[] buffer = new byte[20];
        buffer[1] = 0x01;
        buffer[4] = 0x21; buffer[5] = 0x12; buffer[6] = 0xA4; buffer[7] = 0x42;

        bool result = StunSecurity.ValidateMessageIntegrity(buffer, key);

        Assert.False(result);
    }

    [Fact]
    public void ValidateFingerprint_TooShort_ReturnsFalse()
    {
        byte[] buffer = new byte[4];

        bool result = StunSecurity.ValidateFingerprint(buffer);

        Assert.False(result);
    }
}
