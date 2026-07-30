using OpenAI.Chat;

namespace TerminalAiAssistant;

public class ChatSession
{
    public List<ChatMessage> Messages { get; set; } = [];
    public bool SessionStarted { get; set; }

    public void Reset()
    {
        Messages = [];
        SessionStarted = false;
    }
}
