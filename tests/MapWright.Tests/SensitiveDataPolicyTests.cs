using MapWright.Core.Profile;

namespace MapWright.Tests;

public sealed class SensitiveDataPolicyTests
{
    [Theory]
    [InlineData("ssn", "ssn")]
    [InlineData("OwnerSSN", "ssn")]
    [InlineData("TaxId", "taxid")]
    [InlineData("tax_id", "taxid")]
    [InlineData("BankAccountNumber", "accountnumber")]
    [InlineData("routingNumber", "routing")]
    [InlineData("DOB", "dob")]
    [InlineData("DateOfBirth", "dateofbirth")]
    [InlineData("company", null)]
    [InlineData("Title", null)]
    [InlineData("setting", null)]
    public void Field_names_are_matched_on_tokens(string name, string? term)
    {
        Assert.Equal(term, SensitiveDataPolicy.Default.MatchName(name));
    }

    [Fact]
    public void Generic_leaves_inherit_parent_sensitivity()
    {
        Assert.NotNull(SensitiveDataPolicy.Default.MatchField("Number", "TaxId"));
        Assert.Null(SensitiveDataPolicy.Default.MatchField("type", "TaxId"));
        Assert.Null(SensitiveDataPolicy.Default.MatchField("Number", "Phone"));
    }

    [Fact]
    public void Custom_terms_replace_the_defaults()
    {
        var policy = new SensitiveDataPolicy(["Mother's Maiden Name"]);

        Assert.NotNull(policy.MatchName("mothersMaidenName"));
        Assert.Null(policy.MatchName("ssn"));
    }

    [Theory]
    [InlineData("123-45-6789", "***-**-6789")]
    [InlineData("900123456", "*****3456")]
    [InlineData("4111 1111 1111 1111", "**** **** **** 1111")]
    [InlineData("GB29NWBK60161331926819", "******************6819")]
    [InlineData("1234", "1234")]
    public void Mask_keeps_last_four_characters(string value, string masked)
    {
        Assert.Equal(masked, SensitiveDataPolicy.Mask(value));
    }

    [Theory]
    [InlineData("123-45-6789", true)]
    [InlineData("121000358", true)]
    [InlineData("4111 1111 1111 1111", true)]
    [InlineData("1234567.89", false)]
    [InlineData("2019-04-01", false)]
    [InlineData("(415) 555-0142", false)]
    [InlineData("REP-123456789", false)]
    public void Identifier_like_values_are_detected(string value, bool expected)
    {
        Assert.Equal(expected, SensitiveDataPolicy.LooksLikeIdentifier(value));
    }
}
