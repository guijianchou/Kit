namespace Kit.AiHub.UnitTests;

using System;
using System.Buffers;
using System.IO;
using System.Text.Json;
using Kit.AiHub.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class SanitizerTests
{
    [TestMethod]
    public void JsonFieldsAreRedactedAfterDecodingWhilePreservingStructure()
    {
        var sanitizer = new DataSanitizer();
        JsonElement sanitized;
        using (var document = JsonDocument.Parse("""
            {
              "itemId": "item-000001",
              "api\u004Bey": "fixture-hidden-key",
              "nested": {
                "password": "fixture-hidden-password",
                "ACCESS_TOKEN": "fixture-hidden-token",
                "path": "C:\\Users\\Alice Private\\Documents\\report.txt",
                "notes": ["Bearer fixture-hidden-bearer", "ordinary text"]
              },
              "credentials": {
                "number": 473921,
                "enabled": true,
                "values": ["fixture-hidden-array-value", 28123, null]
              },
              "preserved": [42, 1.25, true, false, null, " \t "]
            }
            """))
        {
            sanitized = sanitizer.SanitizeJson(document.RootElement);
        }

        Assert.AreEqual("item-000001", sanitized.GetProperty("itemId").GetString());
        Assert.AreEqual("[REDACTED_SECRET]", sanitized.GetProperty("apiKey").GetString());
        var nested = sanitized.GetProperty("nested");
        Assert.AreEqual("[REDACTED_SECRET]", nested.GetProperty("password").GetString());
        Assert.AreEqual("[REDACTED_SECRET]", nested.GetProperty("ACCESS_TOKEN").GetString());
        Assert.AreEqual(@"%USERPROFILE%\Documents\report.txt", nested.GetProperty("path").GetString());
        Assert.AreEqual("[REDACTED_SECRET]", nested.GetProperty("notes")[0].GetString());
        Assert.AreEqual("ordinary text", nested.GetProperty("notes")[1].GetString());

        var credentials = sanitized.GetProperty("credentials");
        Assert.AreEqual(JsonValueKind.Object, credentials.ValueKind);
        Assert.AreEqual(JsonValueKind.Number, credentials.GetProperty("number").ValueKind);
        Assert.AreEqual(0, credentials.GetProperty("number").GetInt32());
        Assert.IsFalse(credentials.GetProperty("enabled").GetBoolean());
        Assert.AreEqual("[REDACTED_SECRET]", credentials.GetProperty("values")[0].GetString());
        Assert.AreEqual(0, credentials.GetProperty("values")[1].GetInt32());
        Assert.AreEqual(JsonValueKind.Null, credentials.GetProperty("values")[2].ValueKind);

        var preserved = sanitized.GetProperty("preserved");
        Assert.AreEqual(42, preserved[0].GetInt32());
        Assert.AreEqual(1.25, preserved[1].GetDouble());
        Assert.IsTrue(preserved[2].GetBoolean());
        Assert.IsFalse(preserved[3].GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, preserved[4].ValueKind);
        Assert.AreEqual(" \t ", preserved[5].GetString());
        Assert.IsFalse(sanitized.GetRawText().Contains("fixture-hidden", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("apiKey")]
    [DataRow("api_key")]
    [DataRow("API-KEY")]
    [DataRow("api key")]
    [DataRow("X_API_Key")]
    [DataRow("openai_api_key")]
    [DataRow("Password")]
    [DataRow("clientSecret")]
    [DataRow("refresh_token")]
    [DataRow("Authorization")]
    [DataRow("Proxy-Authorization")]
    [DataRow("Cookie")]
    [DataRow("Set-Cookie")]
    [DataRow("privateKey")]
    [DataRow("ssh_private_key")]
    [DataRow("secret_access_key")]
    public void SensitivePropertyNameVariantsAreRecognized(string propertyName)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(propertyName, "fixture-sensitive-value");
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        var sanitized = new DataSanitizer().SanitizeJson(document.RootElement);
        Assert.AreEqual("[REDACTED_SECRET]", sanitized.GetProperty(propertyName).GetString());
    }

    [TestMethod]
    [DataRow("PRIVATE KEY")]
    [DataRow("RSA PRIVATE KEY")]
    [DataRow("EC PRIVATE KEY")]
    [DataRow("OPENSSH PRIVATE KEY")]
    [DataRow("ENCRYPTED PRIVATE KEY")]
    public void PemPrivateKeyBodiesAreCompletelyRemoved(string label)
    {
        string input = $"-----BEGIN {label}-----\r\nfixture-private-key-body\r\n-----END {label}-----";
        string sanitized = new DataSanitizer().SanitizeText(input);
        Assert.AreEqual("[REDACTED_SECRET]", sanitized);
    }

    [TestMethod]
    public void UnterminatedPrivateKeyCannotLeakItsRemainingBody()
    {
        string sanitized = new DataSanitizer().SanitizeText("-----BEGIN PRIVATE KEY-----\nfixture-private-key-body");
        Assert.AreEqual("[REDACTED_SECRET]", sanitized);
    }

    [TestMethod]
    public void UrlCredentialsAndSensitiveQueryParametersAreRemoved()
    {
        const string input = "https://fixture-user:fixture-password@example.invalid/api?api_key=fixture-query-key&access_token=fixture-query-token&state=public";
        string sanitized = new DataSanitizer().SanitizeText(input);
        Assert.IsFalse(sanitized.Contains("fixture-user", StringComparison.Ordinal));
        Assert.IsFalse(sanitized.Contains("fixture-password", StringComparison.Ordinal));
        Assert.IsFalse(sanitized.Contains("fixture-query", StringComparison.Ordinal));
        StringAssert.Contains(sanitized, "example.invalid/api");
        StringAssert.Contains(sanitized, "state=public");
    }

    [TestMethod]
    public void TextCredentialsHandleQuotedAndEscapedAssignmentsAndHeaders()
    {
        const string input = """
            password="fixture\"quoted-password"
            api_key='fixture-quoted-key'
            token=fixture-bare-token
            Authorization: Basic Zml4dHVyZTpwYXNzd29yZA==
            Cookie: session=fixture-cookie; refresh=fixture-refresh
            """;
        string sanitized = new DataSanitizer().SanitizeText(input);
        Assert.IsFalse(sanitized.Contains("fixture", StringComparison.Ordinal));
        Assert.IsFalse(sanitized.Contains("Zml4dHVyZT", StringComparison.Ordinal));
        StringAssert.Contains(sanitized, "[REDACTED_SECRET]");
    }

    [TestMethod]
    [DataRow(@"C:\Users\Alice Private\Documents\report.txt")]
    [DataRow("C:/Users/Alice Private/Documents/report.txt")]
    [DataRow(@"C:\\Users\\Alice\\Documents\\report.txt")]
    [DataRow(@"D:\Documents and Settings\Alice Private\Documents\report.txt")]
    public void PersonalPathsAreAnonymizedInEverySupportedSlashForm(string path)
    {
        string sanitized = new DataSanitizer().SanitizeText(path);
        Assert.IsFalse(sanitized.Contains("Alice", StringComparison.Ordinal));
        StringAssert.Contains(sanitized, "%USERPROFILE%");
        StringAssert.Contains(sanitized, "report.txt");
    }

    [TestMethod]
    public void EncodedJsonStringValuesAreSanitizedAsDecodedText()
    {
        using var document = JsonDocument.Parse("""
            {
              "description": "password=\"fixture escaped password\"",
              "path": "C:\u005cUsers\u005cAlice\u005cDocuments\u005cfile.txt",
              "payload": "-----BEGIN PRIVATE KEY-----\nfixture-private-data\n-----END PRIVATE KEY-----"
            }
            """);
        var sanitized = new DataSanitizer().SanitizeJson(document.RootElement);
        Assert.IsFalse(sanitized.GetProperty("description").GetString()!.Contains("fixture", StringComparison.Ordinal));
        Assert.AreEqual(@"%USERPROFILE%\Documents\file.txt", sanitized.GetProperty("path").GetString());
        Assert.AreEqual("[REDACTED_SECRET]", sanitized.GetProperty("payload").GetString());
    }

    [TestMethod]
    public void UndefinedOrOversizedJsonFailsClosedWithSafeErrors()
    {
        var sanitizer = new DataSanitizer();
        var undefinedError = Assert.ThrowsExactly<InvalidDataException>(() => sanitizer.SanitizeJson(default));
        Assert.IsNull(undefinedError.InnerException);
        var oversizedError = Assert.ThrowsExactly<InvalidDataException>(() => sanitizer.SanitizeText(new string('a', 1024 * 1024 + 1)));
        Assert.IsNull(oversizedError.InnerException);
        StringAssert.Contains(oversizedError.Message, "limit");

        string deeplyNested = new string('[', 70) + "null" + new string(']', 70);
        using var document = JsonDocument.Parse(deeplyNested, new JsonDocumentOptions { MaxDepth = 128 });
        var depthError = Assert.ThrowsExactly<InvalidDataException>(() => sanitizer.SanitizeJson(document.RootElement));
        Assert.IsNull(depthError.InnerException);
    }

    [TestMethod]
    public void LargeInputsEitherRedactCredentialsOrFailWithoutReturningInput()
    {
        string input = new string('a', 900_000) + "\npassword=fixture-trailing-secret";
        try
        {
            string sanitized = new DataSanitizer().SanitizeText(input);
            Assert.IsFalse(sanitized.Contains("fixture-trailing-secret", StringComparison.Ordinal));
        }
        catch (InvalidDataException exception)
        {
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(exception.Message.Contains("fixture-trailing-secret", StringComparison.Ordinal));
        }
    }
}
