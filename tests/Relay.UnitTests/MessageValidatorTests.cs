using Relay.Application;
using Shouldly;

namespace Relay.UnitTests;

public class MessageValidatorTests
{
    private static readonly MessagingOptions Options = new() { MaxRecipients = 3, MaxBodyBytes = 100 };

    private static SendMessageRequest Valid() => new()
    {
        To = ["to@example.test"],
        Subject = "Hello",
        Text = "Body",
    };

    [Fact]
    public void Valid_request_has_no_errors() =>
        MessageValidator.Validate(Valid(), Options).ShouldBeEmpty();

    [Theory]
    [InlineData("Hello\r\nBcc: victim@example.test")]
    [InlineData("Hello\nX-Injected: 1")]
    [InlineData("Tab\tinside")]
    public void Control_characters_in_subject_are_rejected(string subject) =>
        MessageValidator.Validate(Valid() with { Subject = subject }, Options).ShouldContainKey("subject");

    [Theory]
    [InlineData("victim@example.test\r\nBcc: other@example.test")]
    [InlineData("Display Name <to@example.test>")]
    [InlineData("no-at-sign")]
    [InlineData("user@localhost")]
    [InlineData(" to@example.test")]
    public void Bad_recipient_is_rejected(string address) =>
        MessageValidator.Validate(Valid() with { To = [address] }, Options).ShouldContainKey("to");

    [Fact]
    public void Bad_from_is_rejected() =>
        MessageValidator.Validate(Valid() with { From = "a@b.test\r\nX: y" }, Options).ShouldContainKey("from");

    [Fact]
    public void Missing_recipients_and_subject_and_body_are_all_reported()
    {
        var errors = MessageValidator.Validate(new SendMessageRequest(), Options);

        errors.Keys.ShouldBe(["to", "subject", "body"], ignoreOrder: true);
    }

    [Fact]
    public void Recipient_cap_counts_to_cc_and_bcc() =>
        MessageValidator.Validate(
            Valid() with { Cc = ["c1@example.test", "c2@example.test"], Bcc = ["b@example.test"] }, Options)
            .ShouldContainKey("recipients");

    [Fact]
    public void Body_cap_is_in_utf8_bytes()
    {
        // 34 three-byte characters = 102 bytes > 100, though only 34 chars.
        MessageValidator.Validate(Valid() with { Text = new string('€', 34) }, Options).ShouldContainKey("body");
    }
}
