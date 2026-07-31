# OpenCode Remote

Cliente remoto pessoal que conecta o Telegram a sessões locais do [OpenCode](https://opencode.ai/). O serviço roda no seu computador, usa long polling e mantém a API do OpenCode restrita à interface local, sem abrir portas no roteador.

## Recursos

- Lista fechada de projetos autorizados.
- Criação e retomada de sessões persistidas.
- Seleção de provider e modelo por sessão.
- Modos Plan e Build usando os agentes nativos do OpenCode.
- Respostas finais e atualizações de progresso recebidas por SSE.
- Aprovação de permissões e resposta a perguntas interativas pelo Telegram.
- Conversão de Markdown para HTML compatível com o Telegram.
- Cancelamento da execução atual com `/stop`.
- Persistência local da seleção atual.
- Execução como console ou Windows Service.
- Reconexão ao Telegram com backoff exponencial.

## Como funciona

```text
Telegram
   │ comandos e mensagens
   ▼
TelegramWorker ──► SessionCoordinator ──► OpenCodeClient ──► API local do OpenCode
   ▲                                                      │
   └────────────── IRemoteNotifier ◄── OpenCodeEventWorker ◄── SSE
                              │
                              └── StateStore (JSON local)
```

O `OpenCodeProcessWorker` pode iniciar e encerrar `opencode serve` junto com a aplicação. Se você já gerencia essa instância separadamente, desative essa função com `Remote__OpenCode__ManageProcess=false`.

## Requisitos

- Windows 10 ou superior.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) para desenvolvimento.
- OpenCode instalado e disponível no `PATH`.
- Bot criado pelo [BotFather](https://t.me/BotFather).
- Seu Telegram user ID numérico.

O host genérico do .NET também pode rodar em outros sistemas, mas a instalação como serviço, o caminho padrão do estado e a cobertura atual de testes são orientados ao Windows.

## Instalação

Clone o repositório e restaure as dependências:

```powershell
git clone https://github.com/ianfelps/opencode-remote.git
Set-Location opencode-remote
dotnet restore OpencodeRemote.slnx
```

Copie a configuração de exemplo:

```powershell
Copy-Item .env.example .env
```

Preencha o `.env` com seus dados:

```dotenv
Remote__Telegram__Token=TOKEN_DO_BOT
Remote__Telegram__AllowedUserId=123456789
Remote__OpenCode__Password=UMA_SENHA_FORTE
Remote__Projects__0__Alias=meu-projeto
Remote__Projects__0__Path=C:\caminho\do\projeto
```

Para autorizar mais projetos, repita as duas últimas variáveis incrementando o índice: `Remote__Projects__1__Alias`, `Remote__Projects__1__Path` e assim por diante.

## Configuração

| Variável | Padrão | Descrição |
|---|---|---|
| `Remote__Telegram__Token` | vazio | Token fornecido pelo BotFather. |
| `Remote__Telegram__AllowedUserId` | `0` | Único usuário autorizado a controlar o bot. |
| `Remote__OpenCode__BaseUrl` | `http://127.0.0.1:4096` | Endereço da API local do OpenCode. |
| `Remote__OpenCode__Username` | `opencode` | Usuário da autenticação Basic. |
| `Remote__OpenCode__Password` | vazio | Senha usada pela API local. |
| `Remote__OpenCode__Executable` | `opencode` | Executável usado para iniciar o servidor. |
| `Remote__OpenCode__ManageProcess` | `true` | Define se a aplicação gerencia `opencode serve`. |
| `Remote__StateFile` | `%LOCALAPPDATA%\OpencodeRemote\state.json` | Arquivo JSON com a seleção persistida. |
| `Remote__Projects__N__Alias` | nenhum | Nome curto apresentado no Telegram. |
| `Remote__Projects__N__Path` | nenhum | Caminho absoluto do projeto autorizado. |

Variáveis do sistema têm precedência sobre o `.env`. Durante o desenvolvimento, os segredos também podem ser definidos com .NET User Secrets:

```powershell
dotnet user-secrets set "Remote:Telegram:Token" "TOKEN_DO_BOT" --project src/OpencodeRemote
dotnet user-secrets set "Remote:Telegram:AllowedUserId" "123456789" --project src/OpencodeRemote
dotnet user-secrets set "Remote:OpenCode:Password" "UMA_SENHA_FORTE" --project src/OpencodeRemote
```

## Execução

```powershell
dotnet run --project src/OpencodeRemote
```

Quando `ManageProcess` estiver desativado, inicie o OpenCode separadamente usando o mesmo host, porta e credenciais configurados na aplicação.

Para conectar uma TUI à mesma instância:

```powershell
opencode attach http://127.0.0.1:4096 --dir C:\caminho\do\projeto -u opencode -p SUA_SENHA
```

Evite enviar prompts simultâneos para a mesma sessão pela TUI e pelo Telegram.

## Comandos do bot

| Comando | Ação |
|---|---|
| `/start`, `/help` | Exibe a ajuda. |
| `/projects` | Seleciona um projeto autorizado. |
| `/session`, `/sessions` | Seleciona uma sessão existente. |
| `/new` | Cria uma sessão e ativa Build. |
| `/plan` | Ativa Plan e aceita uma mensagem após o comando. |
| `/build` | Ativa Build e aceita uma mensagem após o comando. |
| `/mode` | Mostra o modo atual. |
| `/model` | Seleciona provider e modelo para a sessão. |
| `/status` | Exibe projeto, sessão, modo e modelo atuais. |
| `/task` | Exibe o progresso da tarefa atual. |
| `/stop` | Interrompe a execução atual. |
| `/clear` | Limpa mensagens removíveis da sessão no Telegram. |

Depois de selecionar projeto e sessão, qualquer mensagem comum é enviada ao OpenCode. Ao concluir uma resposta em Plan, o bot oferece o botão `Implementar este plano`; a implementação só começa após confirmação explícita.

`/clear` remove apenas o histórico visual do Telegram. A sessão do OpenCode não é apagada nem reiniciada, e a API do Telegram normalmente limita a remoção a mensagens das últimas 48 horas.

## Windows Service

Publique uma versão framework-dependent:

```powershell
dotnet publish src/OpencodeRemote -c Release -r win-x64 --self-contained false -o publish
Copy-Item .env publish\.env
```

Em um terminal administrativo, registre o executável publicado:

```powershell
sc.exe create OpenCodeRemote binPath= "C:\caminho\opencode-remote\publish\OpencodeRemote.exe" start= auto
sc.exe start OpenCodeRemote
```

Para remover o serviço:

```powershell
sc.exe stop OpenCodeRemote
sc.exe delete OpenCodeRemote
```

O `.env` fica em texto puro. Restrinja as permissões da pasta publicada à sua conta e à conta usada pelo serviço.

## Desenvolvimento

```powershell
dotnet restore OpencodeRemote.slnx
dotnet build OpencodeRemote.slnx
dotnet test OpencodeRemote.slnx
dotnet format OpencodeRemote.slnx --verify-no-changes
```

Estrutura principal:

```text
src/OpencodeRemote/
├── Configuration/  Opções da aplicação
├── OpenCode/       Cliente HTTP, eventos SSE e processo local
├── Persistence/    Estado persistido em JSON
├── Sessions/       Coordenação e apresentação das sessões
└── Telegram/       Bot, callbacks, notificações e formatação

tests/OpencodeRemote.Tests/
├── OpenCode/
├── Persistence/
├── Sessions/
├── Telegram/
└── TestSupport/
```

## Segurança

- Nunca publique `.env`, tokens, senhas ou o arquivo de estado.
- Mantenha `BaseUrl` em um endereço de loopback, como `127.0.0.1`.
- Autenticação Basic sobre HTTP só é aceitável aqui porque o tráfego permanece local.
- Use uma senha forte para a API e rotacione imediatamente qualquer token exposto.
- O bot ignora mensagens de usuários diferentes de `AllowedUserId`.
- Autorize somente diretórios que podem ser controlados remotamente com segurança.

## Limitações

- O projeto foi desenhado para um único usuário do Telegram.
- Projeto e sessão selecionados são mantidos como um único estado global.
- Botões antigos expiram e deixam de funcionar após reinício da aplicação.
- A integração depende dos endpoints e eventos da versão instalada do OpenCode.
- A supervisão atual não reinicia automaticamente o OpenCode se o processo encerrar depois de ficar saudável.

## Licença

Distribuído sob a licença MIT. Consulte [LICENSE](LICENSE).
