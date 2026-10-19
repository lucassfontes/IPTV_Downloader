# IPTV Downloader Web 2.0.28 Beta

Atualização de código baseada no instalador 2.0.26 Beta.

## Mudanças

- Configuração Conexões por arquivo: 1 (padrão), 2, 4, 8 ou 16. Cada tarefa guarda a escolha feita quando entrou na fila; mudar a configuração não altera trabalhos existentes. Reiniciar do zero aplica a escolha atual.
- Arquivos pequenos podem usar menos partes; cada parte terá pelo menos cerca de 1 MiB. A conclusão de cada parte é registrada. Partes incompletas são preservadas para retomada.
- Sem suporte a faixas HTTP e identificação do arquivo, um download novo usa conexão única. Se o servidor recusar uma parte depois da sondagem ou mudar o arquivo, informa falha e preserva as partes. Escolha 1 conexão e Reiniciar para abandonar a estratégia anterior.
- A união das partes precisa de espaço adicional de aproximadamente o tamanho do arquivo. Backups criados ao reiniciar também ocupam espaço.
- 16 conexões não garantem velocidade maior e podem saturar a rede ou esbarrar nos limites do provedor.


- Um único arquivo é transferido por vez, inclusive após recuperar a fila. A temporada inteira entra na fila. O arquivo atual pode usar várias conexões para suas partes, conforme a configuração.
- Série e temporada abrem pelo clique no cartão; episódio alterna seleção pelo clique ou pelo botão Selecionar. Baixar adiciona diretamente à fila, sem disparar a seleção ou navegação do cartão.
- Mesmos cartões responsivos para séries, temporadas e episódios, com caminho de navegação e botão Voltar.
- Temporadas e episódios ordenados numericamente na inclusão em lote. A lista de downloads mostra o atual e depois a fila na ordem de execução. Itens já presentes na fila não são reposicionados por uma nova inclusão.
- Se o provedor não informar número de episódio, o código tenta extraí-lo do título; sem número reconhecível, o item fica ao final, sem inventar uma numeração.


- Ícone verde de reprodução baseado no esboço aprovado, aplicado no cabeçalho, menu e favicon.

- Filmes, Séries e Downloads em abas lado a lado; Configurações em área separada.
- O contador de Downloads soma itens na fila, em andamento, pausados e com erro. Diminui ao concluir ou parar um item. Concluídos permanecem no histórico; a badge é ocultada ao zerar.
- Menu lateral recolhível no computador: botão alterna entre ícones e rótulos, com nomes ao passar o mouse e preferência salva no navegador. No celular, navegação inferior e cartões responsivos.
- Fila gravada atomicamente em `/appdata/iptv-downloader/data/download-queue.json`.
- Ao iniciar o servidor, retoma os trabalhos que estavam baixando ou aguardando. Trabalhos pausados aguardam ação do usuário.
- Download em `.part`, preservado após falha ou cancelamento. A posição real é lida do arquivo.
- Retomada HTTP com validação de Content-Range e identificação do arquivo por ETag forte ou Last-Modified. Se não for possível retomar com segurança, informa o problema e oferece reiniciar.
- Tamanho recebido precisa coincidir com o tamanho informado pelo servidor. Se o tamanho não for informado, a conclusão fica sem confirmação e o parcial é preservado.
- Um SHA-256 local é registrado após uma transferência completa e conferido antes de reutilizar um arquivo final. Isso detecta alterações locais posteriores, mas não substitui um checksum fornecido pela origem nem garante que o vídeo original não tenha defeitos.
- Arquivos antigos sem registro de conclusão não são considerados completos automaticamente.
- A área Downloads mostra toda a fila e o histórico persistido, sem o limite anterior de 200 registros.
- Botões por arquivo: Pausar, Continuar, Parar, Tentar novamente e Reiniciar download, conforme o estado. Pausar um item que aguarda remove-o da fila ativa até Continuar. Parar preserva o parcial.
- Barra individual: vermelha no erro, azul na pausa, verde na conclusão. Erro e pausa mantêm a porcentagem alcançada; Reiniciar zera a porcentagem.
- Tentar novamente e Reiniciar download aparecem quando há falha ou parada. Reiniciar guarda arquivos anteriores com sufixo `.backup-...`, consumindo espaço adicional.
- Mantidas as pastas automáticas, nomes sem acentos e ordenação numérica dos episódios.

## Estado da validação

Sintaxe JavaScript validada com `node --check`. Revisão estática das alterações C# feita, mas **compilação .NET e execução real não foram realizadas**: este ambiente não tem o SDK .NET nem Docker. A tentativa de teste visual automatizado foi bloqueada pela ausência do navegador Chromium. Portanto, a interface também precisa de validação visual em dispositivo real. Esta é uma versão Beta para teste, não uma atualização já validada para produção.

A transferência usa .NET: uma conexão por padrão, ou partes paralelas configuráveis dentro do mesmo arquivo. Compare a velocidade com a versão anterior; o antigo caminho via curl foi removido.

## Testar sem substituir a instalação atual

Siga primeiro `TESTE_RAPIDO_ZIMAOS.md`. O arquivo `compose.test.yaml` usa a porta 5079 e pastas separadas. O mesmo roteiro explica o ícone no cartão do ZimaOS e como atualizar seu endereço.

## Preparar a atualização

1. Extraia o pacote em uma pasta nova, separada da instalação atual.
2. Confira no aplicativo atual do ZimaOS os volumes de dados e de mídia, a porta e o nome da pasta principal. O `compose.yaml` usa os caminhos do YAML anterior, que precisam corresponder à instalação em uso.
3. Se necessário, crie um arquivo `.env` nesta pasta com `IPTV_APPDATA`, `IPTV_STORAGE`, `IPTV_ROOT_NAME` e `IPTV_PORT`. Para a instalação pelo script automático, o diretório de dados pode ser `/DATA/AppData/iptv-downloader-web-facil/dados`, diferente do padrão do Compose.
4. Construa a nova imagem sem parar o aplicativo existente:

```bash
docker compose build
```

Se o build falhar, mantenha a instalação anterior e envie o erro. Não prossiga.

5. Depois do build, pause os downloads na versão antiga, faça uma cópia da pasta de dados e pare o contêiner antigo pelo ZimaOS. A fila que só existe na memória da 2.0.26 não pode ser migrada automaticamente; registre os itens antes de parar. Não remova os volumes.
6. Inicie na pasta nova:

```bash
docker compose up -d
docker compose logs --tail=80
```

Caso o nome `iptv-downloader-web` já pertença ao contêiner antigo, renomeie o contêiner antigo parado pelo ZimaOS antes de iniciar. Não execute duas versões simultaneamente sobre a mesma pasta de downloads.

Acesse `http://IP-DO-ZIMAOS:5078`, ou a porta escolhida. Atualize a página sem cache para carregar o novo CSS e JavaScript. A imagem `ghcr.io/lucassfontes/iptv_downloader:latest` não foi alterada por este pacote.

Para voltar: pare a nova versão e inicie o contêiner anterior. A versão antiga não conhece os novos controles de integridade e retomada.

## Checklist de teste

- [ ] Compilar a imagem .NET sem erros.
- [ ] Conectar a conta salva e conferir o mesmo destino de mídia.
- [ ] Baixar um arquivo pequeno e conferir tamanho, conclusão e reprodução.
- [ ] Interromper o contêiner durante um download de teste, iniciá-lo novamente e confirmar retomada da fila sem abrir o navegador. Não é necessário cortar a energia física.
- [ ] Confirmar que servidor sem suporte à retomada mostra erro e não concatena uma resposta inteira ao parcial.
- [ ] Confirmar tratamento de mudança do ETag/Last-Modified ou tamanho remoto.
- [ ] Cancelar e tentar novamente; confirmar que o parcial foi preservado.
- [ ] Reiniciar download e confirmar preservação do arquivo anterior como backup.
- [ ] Conferir que arquivo antigo incompleto não recebe automaticamente status Concluído.
- [ ] Conferir erro em transferência truncada, tamanho desconhecido e falta de espaço.
- [ ] Testar pausa e retomada, fila com vários arquivos e episódios de uma temporada.
- [ ] Abrir Catálogo, Downloads e Configurações no celular e no computador; testar busca, filtros, navegação e botões sem rolagem horizontal.

O aplicativo continua sem autenticação própria: mantenha o acesso na rede local. A fila persistida contém as informações necessárias para autenticar os downloads, como as contas já salvas; proteja a pasta de dados.

## Testes adicionais de partes (pendentes no ambiente real)

- [ ] Testar 1, 2, 4, 8 e 16 conexões num arquivo de teste, comparando checksum com a origem.
- [ ] Interromper e retomar durante as partes e durante a união.
- [ ] Testar provedor sem Range, retorno 200 inesperado, faixa incorreta, timeout, 429 e mudança de ETag.
- [ ] Confirmar que um arquivo seguinte só começa após terminar ou falhar o atual.
- [ ] Testar pausa, continuar, parar, falha de uma parte e reiniciar com menos conexões.
- [ ] Validar espaço insuficiente durante a união e preservação dos parciais.
