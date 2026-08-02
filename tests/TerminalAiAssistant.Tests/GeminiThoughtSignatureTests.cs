using System.ClientModel.Primitives;
using OpenAI.Chat;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class GeminiThoughtSignatureTests
{
    private const string SignatureJson = "{\"google\":{\"thought_signature\":\"sig-123\"}}";

    [Fact]
    public void JsonPatch_StoresAndReadsExtraContentValue()
    {
#pragma warning disable SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
        var patch = new JsonPatch();
        patch.Set("$.extra_content"u8, BinaryData.FromString(SignatureJson));

        Assert.True(patch.Contains("$.extra_content"u8));
        Assert.Equal(SignatureJson, patch.GetJson("$.extra_content"u8).ToString());
        Assert.False(patch.Contains("$.other_field"u8));
#pragma warning restore SCME0001
    }

    [Fact]
    public void ChatToolCall_WithExtraContentPatch_SerializesExtraContent()
    {
#pragma warning disable SCME0001, OPENAI001 // Types are for evaluation purposes only and are subject to change or removal in future updates.
        var toolCall = ChatToolCall.CreateFunctionToolCall("call-1", "Read", BinaryData.FromString("{\"file_path\":\"src/Program.cs\"}"));
        toolCall.Patch.Set("$.extra_content"u8, BinaryData.FromString(SignatureJson));

        string json = ((IPersistableModel<ChatToolCall>)toolCall).Write(new ModelReaderWriterOptions("J")).ToString();

        Assert.Contains("\"extra_content\":{\"google\":{\"thought_signature\":\"sig-123\"}}", json);
        Assert.Contains("\"call-1\"", json);
#pragma warning restore SCME0001, OPENAI001
    }

    [Fact]
    public void AssistantChatMessage_CarriesExtraContentThroughSerialization()
    {
#pragma warning disable SCME0001, OPENAI001 // Types are for evaluation purposes only and are subject to change or removal in future updates.
        var toolCall = ChatToolCall.CreateFunctionToolCall("call-1", "Read", BinaryData.FromString("{}"));
        toolCall.Patch.Set("$.extra_content"u8, BinaryData.FromString(SignatureJson));

        var message = new AssistantChatMessage(new[] { toolCall });
        string json = ((IPersistableModel<AssistantChatMessage>)message).Write(new ModelReaderWriterOptions("J")).ToString();

        Assert.Contains("\"extra_content\":{\"google\":{\"thought_signature\":\"sig-123\"}}", json);
#pragma warning restore SCME0001, OPENAI001
    }

    [Fact]
    public void ChatToolCall_WithoutExtraContentPatch_OmitsField()
    {
#pragma warning disable OPENAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
        var toolCall = ChatToolCall.CreateFunctionToolCall("call-1", "Read", BinaryData.FromString("{}"));

        string json = ((IPersistableModel<ChatToolCall>)toolCall).Write(new ModelReaderWriterOptions("J")).ToString();

        Assert.DoesNotContain("extra_content", json);
#pragma warning restore OPENAI001
    }
}
