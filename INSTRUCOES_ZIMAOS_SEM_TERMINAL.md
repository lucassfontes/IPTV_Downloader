# IPTV Downloader Web 2.0.40 — ZimaOS sem terminal

## 1) Atualizar o GitHub
Abra https://github.com/lucassfontes/IPTV_Downloader e envie os arquivos DE DENTRO desta pasta para a raiz do repositório (não a pasta ZIP inteira). Mantenha a pasta `Web`, a pasta `Original_Avalonia_2.0.26`, o `Dockerfile` e `.github/workflows/docker-publish.yml`. Atualize arquivos existentes com as versões desta pasta. O arquivo de workflow pode ser criado/editado pelo navegador no caminho indicado.

> Atenção: se o GitHub web não permitir substituir arquivos em massa, use a função de upload apropriada no GitHub ou GitHub Desktop. Não publique arquivos pessoais de teste como senhas, contas ou pastas de dados. Este pacote não contém sua conta IPTV salva.

## 2) Gerar imagem
Abra https://github.com/lucassfontes/IPTV_Downloader/actions e veja o workflow "Publicar IPTV Downloader 2.0.40". Se necessário, em `Actions`, execute `Run workflow` na branch main. Confirme o resultado verde. No pacote `iptv_downloader` em GitHub Packages, ajuste a visibilidade para **Public**; caso contrário o ZimaOS precisa de login no registro.

## 3) Instalar pelo painel ZimaOS
No ZimaOS, escolha instalar aplicativo personalizado, opção importar/colar Docker Compose. Abra `COMPOSE_PARA_ZIMAOS.yaml` e cole o conteúdo. A imagem `ghcr.io/lucassfontes/iptv_downloader:2.0.40` deve estar publicada antes. Inicie/instale o app. Se já existe contêiner `iptv-downloader-web`, atualize/recrie o existente sem criar dois contêineres com esse nome.

Acesso: `http://IP-DO-ZIMAOS:5088`

Configurações: `/DATA/AppData/iptv-downloader-web`

Downloads no host: `/DATA/Downloads/iptv_dowload`. Dentro do Docker: `/storage/iptv_dowload`.

Se o armazenamento correto do seu ZimaOS estiver em outro caminho, altere **somente o lado esquerdo** da montagem `/DATA/Downloads:/storage` antes da instalação. Você deve garantir acesso e espaço nesse caminho.

## Limitação do botão Ver pasta
O navegador no computador não pode abrir o gerenciador de arquivos do host ZimaOS. Para acessar os vídeos, use o gerenciador de arquivos do ZimaOS no diretório montado.

## Observações
A versão 2.0.40 foi editada em arquivos de código-fonte; executar Docker build e testar no ZimaOS ainda é necessário. A instalação não foi feita automaticamente no seu servidor. Não remova as pastas persistentes ao recriar o app.
