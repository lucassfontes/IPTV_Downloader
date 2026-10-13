# IPTV Downloader C# — 2.0.21

## Alterações 2.0.21

- O card **Todos os filmes** agora fica sempre como o primeiro card no topo das categorias de Filmes.
- O card **Todas as séries** agora fica sempre como o primeiro card no topo das categorias de Séries.
- Adicionada paginação no final de cada página com os botões **Página anterior** e **Próxima página**.
- Mantidas as categorias da lista IPTV, busca global, progresso de download, tamanho baixado/total e tempo restante.


Reescrita em C# / .NET 8 + Avalonia para Linux e Windows.

## Alterações 2.0.19

- Tempo restante estimado durante o download.
- Opção “Todos os filmes” / “Todas as séries” no topo das categorias.
- Ícones de categoria trocados por 🎬 e 📺.

## Alterações 2.0.16

- Busca global em todos os filmes e séries, independentemente da categoria ou página aberta.
- Barra de progresso muda para verde quando a fila de downloads chega a 100%.
- Exibição do tamanho baixado e tamanho total do arquivo durante o download.
- Mantida a navegação por categorias da lista IPTV.

## Alterações 2.0.15

- Novo ícone oficial escolhido pelo usuário: **Ícone 3D de download de filmes**.
- Ícone aplicado à janela do Avalonia.
- Ícone `.ico` multi-resolução aplicado ao executável Windows.
- PNG 512x512 incluído para integração com Linux/Zorin.
- Novo `instalar-linux.sh` publica o aplicativo, instala o ícone no sistema e cria o atalho no menu de aplicativos.

### Instalar no Zorin/Linux com ícone no sistema

Na raiz do projeto:

```bash
chmod +x instalar-linux.sh
./instalar-linux.sh
```

Depois procure por **IPTV Downloader** no menu do sistema.

## Alterações 2.0.13

- Corrige a duração exibida nos cards dos filmes.
- Prioriza `duration_secs` retornado pelo servidor Xtream quando disponível.
- Interpreta corretamente formatos como `01:28:00`, `88 min` e valores numéricos.
- Exibe duração de forma legível, por exemplo `1h 28min`.
- Remove completamente a geração de arquivos `.nfo` em filmes, séries e episódios.
- O download salva somente os arquivos de vídeo e a estrutura de pastas correspondente.

## Executar

Na pasta do projeto:

```bash
cd src/IPTVDownloader
dotnet restore
dotnet run
```

Ou pela raiz:

```bash
dotnet run --project ./src/IPTVDownloader/IPTVDownloader.csproj
```

- Exibe **Carregando...** ao abrir/carregar Filmes, Séries, Favoritos e Histórico.
- **[Nenhum item encontrado]** agora aparece somente após uma busca sem resultados.



## Correções 2.0.22
- Corrige erro 404 ao colar URL Xtream completa (`get.php`, `player_api.php`, `xmltv.php` ou `panel_api.php`) no campo Servidor.
- O download passa a respeitar `container_extension` do catálogo (`mp4`, `mkv`, `ts` etc.) em vez de forçar `.mp4`, evitando 404 em filmes e episódios.
- A mensagem de erro de conexão 404 ficou mais clara.
