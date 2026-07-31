# OpenCode Remote

<div align="center">
<pre>
                                 ▄
█▀▀█ █▀▀█ █▀▀█ █▀▀▄ █▀▀▀ █▀▀█ █▀▀█ █▀▀█
█  █ █  █ █▀▀▀ █  █ █    █  █ █  █ █▀▀▀
▀▀▀▀ █▀▀▀ ▀▀▀▀ ▀  ▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀
              R E M O T E
</pre>
</div>

CLI que conecta o Telegram a sessões locais do [OpenCode](https://opencode.ai/). Execute `opencode-remote` dentro de um repositório para iniciar o servidor, acompanhar a atividade em um painel ao vivo e controlar a sessão pelo celular.

## Recursos

- Instalação global como .NET tool no Windows, Linux e macOS.
- Detecção automática da raiz do repositório Git atual.
- Painel ao vivo com conexão, sessão, tarefa, etapa, atividade e alterações.
- Criação e retomada de sessões persistidas.
- Troca de projeto pelo Telegram usando os projetos conhecidos pelo OpenCode.
- Seleção de provider e modelo por sessão.
- Modos Plan e Build usando os agentes nativos do OpenCode.
- Respostas finais e atualizações de progresso recebidas por SSE.
- Aprovação de permissões e resposta a perguntas interativas pelo Telegram.
- Conversão de Markdown para HTML compatível com o Telegram.
- Cancelamento da execução atual com `/stop`.
- Persistência independente da seleção de cada projeto.
- Configuração global interativa com segredos mascarados.
- Bloqueio de execuções concorrentes usando o mesmo bot e servidor.
- Reconexão ao Telegram com backoff exponencial.

## Como funciona

```text
Telegram
   │ comandos e callbacks
   ▼
┌──────────────────────┐      ┌──────────────────────┐
│ TelegramWorker       │─────►│ SessionCoordinator   │◄────► StateStore
└──────────────────────┘      └──────────┬───────────┘       estado persistido
                                        │
                                        ▼
                             ┌──────────────────────┐
                             │ OpenCodeClient       │─────► API local do OpenCode
                             └──────────────────────┘                 │
                                                                    │ SSE global
                                                                    ▼
                             ┌──────────────────────┐      ┌──────────────────────┐
Telegram ◄───────────────────│ OpenCodeEventWorker  │─────►│ RuntimeStatusStore   │
 respostas e solicitações    └──────────────────────┘      └──────────┬───────────┘
                                                                       │
                                                                       ▼
                                                               painel no terminal
```

Ao iniciar, a CLI resolve o diretório informado ou a raiz Git atual e carrega o estado persistido daquela execução. O `OpenCodeProcessWorker` verifica a API configurada e, por padrão, inicia `opencode serve` quando nenhuma instância está disponível. Se você já gerencia o servidor separadamente, use `Remote__OpenCode__ManageProcess=false`.

O fluxo principal é dividido entre estes componentes:

- `TelegramWorker` recebe mensagens e callbacks, valida o usuário autorizado e serializa as interações de cada chat.
- `SessionCoordinator` mantém o projeto, a sessão, o agente e o modelo selecionados, além de impedir operações incompatíveis enquanto uma tarefa está em execução.
- `OpenCodeClient` traduz as ações do bot para chamadas HTTP autenticadas à API do OpenCode.
- `OpenCodeEventWorker` acompanha o stream SSE global, filtra eventos pelo projeto e pela sessão ativos e envia respostas, progresso, permissões e perguntas ao Telegram.
- `StateStore` salva a seleção atual em JSON para que projeto, sessão e preferências sobrevivam à reinicialização.
- `RuntimeStatusStore` mantém informações transitórias usadas pelo painel, como conexão, tarefa ativa, etapa atual e último erro.

Quando `/move` é usado, o bot consulta `GET /project`, mostra os projetos conhecidos pelo servidor e valida novamente o destino quando o botão é pressionado. A troca preserva o estado geral, limpa a sessão ativa, atualiza o painel e abre o seletor de sessões do novo projeto. Eventos e botões pertencentes ao contexto anterior são ignorados para evitar que respostas de projetos diferentes sejam misturadas.

## Instalação

- Windows, Linux ou macOS.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- OpenCode instalado e disponível no `PATH`.
- Bot criado pelo [BotFather](https://t.me/BotFather).
- Seu Telegram user ID numérico.

Enquanto o pacote não estiver publicado no NuGet, gere e instale a ferramenta a partir do repositório:

```bash
git clone https://github.com/ianfelps/opencode-remote.git
cd opencode-remote
dotnet pack src/OpencodeRemote -c Release -o artifacts
dotnet tool install --global --add-source ./artifacts OpenCodeRemote
```

Depois que o pacote estiver disponível no NuGet, a instalação será:

```bash
dotnet tool install --global OpenCodeRemote
```

Configure as credenciais na primeira execução:

```bash
opencode-remote config
```

Para atualizar uma instalação feita pelo pacote local:

```bash
dotnet pack src/OpencodeRemote -c Release -o artifacts
dotnet tool update --global --add-source ./artifacts OpenCodeRemote
```

O comando `config` solicita o token do bot, seu Telegram user ID e as credenciais usadas pelo servidor local do OpenCode. Token e senha não são exibidos durante a digitação.

Para revisar a configuração sem revelar os segredos:

```bash
opencode-remote config show
```

O arquivo global fica em `%APPDATA%\OpenCodeRemote\config.json` no Windows e no diretório de configuração do usuário no Linux/macOS. Ele contém segredos em texto simples; restrinja o acesso ao seu usuário.

## Execução

Entre no projeto e inicie a CLI:

```bash
cd meu-projeto
opencode-remote
```

A CLI procura uma raiz Git a partir do diretório atual. Também é possível informar explicitamente o diretório:

```bash
opencode-remote run /caminho/do/projeto
```

Enquanto o comando estiver aberto, o bot permanece disponível no Telegram. Use `Ctrl+C` para encerrar o Remote e o processo `opencode serve` iniciado por ele.

Para substituir o painel por logs:

```bash
opencode-remote --no-dashboard
opencode-remote --verbose
```

O painel mostra:

- Estado do servidor OpenCode e do Telegram.
- Projeto, sessão e modo selecionados.
- Tempo da execução atual.
- Etapa, ferramenta ativa e resumo das alterações.
- Esperas por permissão ou resposta no Telegram.
- Último erro observado.

## Configuração

| Variável | Padrão | Descrição |
|---|---|---|
| `Remote__Telegram__Token` | configurado por `config` | Token fornecido pelo BotFather. |
| `Remote__Telegram__AllowedUserId` | configurado por `config` | Único usuário autorizado a controlar o bot. |
| `Remote__OpenCode__BaseUrl` | `http://127.0.0.1:4096` | Endereço da API local do OpenCode. |
| `Remote__OpenCode__Username` | `opencode` | Usuário da autenticação Basic. |
| `Remote__OpenCode__Password` | vazio | Senha usada pela API local. |
| `Remote__OpenCode__Executable` | `opencode` | Executável usado para iniciar o servidor. |
| `Remote__OpenCode__ManageProcess` | `true` | Define se a aplicação gerencia `opencode serve`. |

Variáveis do sistema têm precedência sobre a configuração global. Durante o desenvolvimento, os segredos também podem ser definidos com .NET User Secrets:

```powershell
dotnet user-secrets set "Remote:Telegram:Token" "TOKEN_DO_BOT" --project src/OpencodeRemote
dotnet user-secrets set "Remote:Telegram:AllowedUserId" "123456789" --project src/OpencodeRemote
dotnet user-secrets set "Remote:OpenCode:Password" "UMA_SENHA_FORTE" --project src/OpencodeRemote
```

Para executar o código-fonte diretamente:

```bash
dotnet run --project src/OpencodeRemote -- run /caminho/do/projeto
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
| `/move` | Troca para outro projeto conhecido pelo OpenCode e abre o seletor de sessões. |
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

Depois de selecionar uma sessão, qualquer mensagem comum é enviada ao OpenCode. Ao concluir uma resposta em Plan, o bot oferece o botão `Implementar este plano`; a implementação só começa após confirmação explícita.

`/move` troca o contexto ativo do bot, mas não transfere a conversa atual para o novo projeto. Depois da troca, selecione uma sessão existente ou use `/new`. Todos os projetos retornados por `GET /project` do servidor OpenCode ficam disponíveis ao usuário autorizado.

`/clear` remove apenas o histórico visual do Telegram. A sessão do OpenCode não é apagada nem reiniciada, e a API do Telegram normalmente limita a remoção a mensagens das últimas 48 horas.

## Desenvolvimento

```powershell
dotnet restore OpencodeRemote.slnx
dotnet build OpencodeRemote.slnx
dotnet test OpencodeRemote.slnx
dotnet format OpencodeRemote.slnx --verify-no-changes
dotnet pack src/OpencodeRemote -c Release -o artifacts
```

Instalação local do pacote gerado:

```bash
dotnet tool install --global --add-source artifacts OpenCodeRemote
```

Estrutura principal:

```text
src/OpencodeRemote/
├── Cli/            Comandos, configuração, projeto e lock
├── Configuration/  Opções da aplicação
├── OpenCode/       Cliente HTTP, eventos SSE e processo local
├── Persistence/    Estado persistido em JSON
├── Runtime/        Estado e painel ao vivo
├── Sessions/       Coordenação e apresentação das sessões
└── Telegram/       Bot, callbacks, notificações e formatação

tests/OpencodeRemote.Tests/
├── Cli/
├── OpenCode/
├── Persistence/
├── Runtime/
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
- O usuário autorizado pode acessar todos os projetos conhecidos pela instância configurada do OpenCode; conecte o bot somente a uma instância que não exponha diretórios indevidos.

## Limitações

- O projeto foi desenhado para um único usuário do Telegram.
- Apenas uma instância pode usar a configuração global por vez.
- O comando precisa permanecer aberto; esta versão não instala daemon ou serviço.
- Botões antigos expiram e deixam de funcionar após reinício da aplicação.
- A integração depende dos endpoints e eventos da versão instalada do OpenCode.

## Licença

Distribuído sob a licença MIT. Consulte [LICENSE](LICENSE).
