namespace OpencodeRemote.Telegram;

public static class ToolProgressFormatter
{
    public static string Format(string tool, string status)
    {
        var activity = tool.ToLowerInvariant() switch
        {
            "read" or "glob" or "grep" or "list" => "Analisando o projeto",
            "bash" or "shell" => "Executando comandos",
            "edit" or "write" or "apply_patch" => "Alterando arquivos",
            "webfetch" or "websearch" => "Consultando fontes externas",
            "task" => "Executando uma etapa delegada",
            "todowrite" => "Atualizando o plano de trabalho",
            "skill" => "Preparando a tarefa",
            _ => "Executando uma operação",
        };
        return status == "error" ? $"Não foi possível concluir: {activity.ToLowerInvariant()}." : activity;
    }
}
