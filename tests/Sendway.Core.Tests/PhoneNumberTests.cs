using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("+821012345678", "82", "1012345678")]
    [InlineData("+15551234567", "1", "5551234567")]
    [InlineData("+79161234567", "7", "9161234567")]
    [InlineData("+447911123456", "44", "7911123456")]
    [InlineData("+81312345678", "81", "312345678")]
    [InlineData("+85291234567", "852", "91234567")]
    [InlineData("+3538612345678", "353", "8612345678")]
    public void Parse_SplitsTheCountryCodeFromTheNationalNumber(string value, string countryCode, string nationalNumber)
    {
        var number = PhoneNumber.Parse(value);

        Assert.Equal(countryCode, number.CountryCode);
        Assert.Equal(nationalNumber, number.NationalNumber);
        Assert.Equal(value, number.ToString());
    }

    [Theory]
    [InlineData("01012345678")]
    [InlineData("+82 10-1234-5678")]
    [InlineData("+0821012345678")]
    [InlineData("+82123")]
    [InlineData("+8210123456789012")]
    [InlineData("")]
    public void Parse_WhenNotInE164Form_Throws(string value)
    {
        Assert.Throws<InvalidRecipientException>(() => PhoneNumber.Parse(value));
    }
}
