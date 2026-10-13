**IPTV Downloader Web — v2.0.26 Beta**  
**Interface web com motor original em C# para gerenciar e baixar filmes e séries de listas IPTV autorizadas.**  
A interface abre no navegador, mas os downloads são processados **no computador ou servidor em que o aplicativo está executando**. A versão Avalonia original continua disponível em Original_Avalonia_2.0.26/.  
***Beta:*** * esta versão web ainda não reproduz todas as funções da versão desktop, como o player incorporado. Use somente listas e conteúdos para os quais você tenha autorização.*  
**Instalação rápida no ZimaOS (Docker)**  
É o método recomendado para executar o IPTV Downloader no servidor sem instalar o SDK .NET e sem copiar o código C# para o ZimaOS. A imagem já compilada é distribuída pelo GitHub Container Registry (GHCR).  
**Arquivos para baixar**  
- [**⬇️ Baixar IPTV_Downloader_Docker.yml — instalação **](https://raw.githubusercontent.com/lucassfontes/IPTV_Downloader/main/IPTV_Downloader_Docker.yml "https://raw.githubusercontent.com/lucassfontes/IPTV_Downloader/main/IPTV_Downloader_Docker.yml")  
- [⬇️ Baixar o projeto completo (ZIP)](https://github.com/lucassfontes/IPTV_Downloader/archive/refs/heads/main.zip "https://github.com/lucassfontes/IPTV_Downloader/archive/refs/heads/main.zip")  
- [Ver o código-fonte no GitHub](https://github.com/lucassfontes/IPTV_Downloader "https://github.com/lucassfontes/IPTV_Downloader")  
***Importante:*** * o link do YAML funciona depois que o arquivo * *IPTV_Downloader_Docker.yml* * estiver salvo na raiz da branch * *main* * deste repositório. Se o download mostrar * ***404*** *, publique o YAML antes de prosseguir.*  
**Passo a passo pelo painel do ZimaOS**  
1. **Verifique a imagem Docker:** em [GitHub Packages, confira se iptv_downloader está definido como  **Public**. A publicação é realizada pelo workflow do GitHub Actions.](https://github.com/lucassfontes?tab=packages "https://github.com/lucassfontes?tab=packages")  
2. **Baixe o YAML** pelo link acima.  
3. Entre no ZimaOS e abra **Aplicativos → Instalar aplicativo personalizado → Importar**.  
4. Importe IPTV_Downloader_Docker.yml e confira os campos antes de instalar.  
5. **Confira o SSD.** O arquivo está configurado para a pasta do servidor /media/devmon/MEDIA, montada dentro do contêiner em /storage. Se o seu SSD estiver em outro caminho, edite a primeira parte do volume indicado abaixo.  
6. Confira que a **porta 5078** está livre e mapeada para a porta interna  **5078**. Se o ZimaOS não preencher o endereço de Web UI automaticamente, configure http://IP-DO-ZIMAOS:5078/.  
7. Clique em **Instalar** e aguarde a imagem ser baixada e o contêiner iniciar.  
8. Acesse o aplicativo pelo ícone do ZimaOS ou pelo navegador. Por exemplo: http://192.168.0.8:5078/**se esse ainda for o IP do seu servidor**.  
**Configuração do Docker**  
| | |  
|-|-|  
| **Item** | **Configuração atual** |   
| Imagem | ghcr.io/lucassfontes/iptv_downloader:latest |   
| Porta do ZimaOS → contêiner | 5078 → 5078 |   
| Endereço web | http://IP-DO-ZIMAOS:5078/ |   
| Ícone | [icon.png no GitHub](https://raw.githubusercontent.com/lucassfontes/IPTV_Downloader/main/Web/wwwroot/icon.png "https://raw.githubusercontent.com/lucassfontes/IPTV_Downloader/main/Web/wwwroot/icon.png") |   
| SSD/HD no ZimaOS | /media/devmon/MEDIA |   
| Mesmo SSD dentro do Docker | /storage |   
| Nome da pasta principal | iptv_dowload |   
| Dados persistentes | /DATA/AppData/iptv-downloader-web |   
   
**Para trocar o disco**, altere somente o lado esquerdo deste volume no arquivo YAML, mantendo /storage à direita:  
volumes:  
   - /DATA/AppData/iptv-downloader-web:/appdata  
   - /media/devmon/MEDIA:/storage  
   
Exemplo: se o disco no ZimaOS estiver em /media/SSD2, use /media/SSD2:/storage. Não é preciso criar as subpastas de filmes e séries manualmente, mas o Docker precisa ter permissão de escrita no disco.  
**Onde os filmes e séries são salvos?**  
O C# recebe a pasta **pai** /storage e cria automaticamente a pasta principal  **iptv_dowload** e as subpastas de conteúdo. A organização é semelhante a:  
SSD ou HD (/media/devmon/MEDIA/)  
 └── iptv_dowload/  
     ├── filmes/  
     │   └── Nome do Filme.mkv  
     └── series/  
         └── Nome da Serie/  
             └── Temporada 01/  
                 └── S01E01.mkv  
   
Se iptv_dowload já existir, o aplicativo reutiliza essa pasta; a instalação do Docker não apaga automaticamente o conteúdo do volume. Antes de trocar caminhos ou atualizar, faça backup dos arquivos importantes.  
**Onde ficam as contas Xtream e as configurações?**  
As contas salvas, configurações e dados do aplicativo ficam **fora do contêiner**, no servidor ZimaOS, pois o YAML monta /DATA/AppData/iptv-downloader-web:/appdata.  
/DATA/AppData/iptv-downloader-web/  
 └── iptv-downloader/  
     ├── data/  
     │   ├── contas.json       # contas Xtream lembradas  
     │   ├── config.json       # configurações  
     │   ├── favoritos.json    # favoritos  
     │   └── historico.json    # histórico salvo  
     └── cache/  
   
Uma conta só é gravada em contas.json quando a opção para lembrar/salvar a conta for usada. **As senhas Xtream nesse arquivo não são criptografadas:** proteja o diretório, não coloque o JSON no GitHub e não compartilhe backups publicamente. Para manter esses dados ao atualizar a imagem Docker, preserve o volume /DATA/AppData/iptv-downloader-web:/appdata.  
**Atualizar a versão no ZimaOS**  
Após uma nova compilação/publicação bem-sucedida no GitHub Actions, atualize/recrie o contêiner para baixar a nova imagem :latest, **preservando os volumes** do SSD e de /appdata. Publicar uma atualização no GitHub, sozinho, não atualiza automaticamente um contêiner já em execução.  
**Diagnóstico**  
Se o aplicativo não abrir:  
1. Confirme se o contêiner iptv-downloader-web está **Em execução** no painel do ZimaOS.  
2. Confirme que o pacote GHCR é público e a porta 5078 não está em uso por outro aplicativo.  
3. Confira se o endereço da Web UI aponta para o IP local correto do ZimaOS.  
4. Veja os registros do contêiner no ZimaOS ou execute, caso tenha terminal:  
5. docker logs --tail 100 iptv-downloader-web  
   
**Instalação no Zorin OS / Ubuntu / Linux (sem Docker)**  
1. Instale o [SDK .NET 8.](https://dotnet.microsoft.com/download/dotnet/8.0 "https://dotnet.microsoft.com/download/dotnet/8.0")  
2. [Baixe o projeto completo e extraia o ZIP.](https://github.com/lucassfontes/IPTV_Downloader/archive/refs/heads/main.zip "https://github.com/lucassfontes/IPTV_Downloader/archive/refs/heads/main.zip")  
3. Abra um terminal na raiz da pasta extraída.  
4. Execute:  
5. bash iniciar-web-linux.sh  
   
6. Acesse http://localhost:5078/ **no próprio computador**.  
Para iniciar manualmente na raiz do repositório:  
dotnet restore Web/IPTVDownloader.Web.csproj  
 dotnet run --project Web/IPTVDownloader.Web.csproj --urls http://127.0.0.1:5078  
   
**Instalação no Windows (sem Docker)**  
1. Instale o [SDK .NET 8.](https://dotnet.microsoft.com/download/dotnet/8.0 "https://dotnet.microsoft.com/download/dotnet/8.0")  
2. Baixe e extraia o ZIP completo do projeto.  
3. Execute iniciar-web-windows.bat na raiz extraída.  
4. Acesse http://localhost:5078/ no navegador.  
**Recursos disponíveis na beta**  
- Conexão Xtream com servidor, usuário e senha, com opção de lembrar a conta.  
- Catálogo de filmes, séries, temporadas e episódios.  
- Busca sem diferenciar acentos, filtros por categoria, índice alfabético e paginação.  
- Fila para filmes, episódios, temporadas e séries inteiras.  
- Motor de download original em C#: um download por vez, progresso e velocidade, pausar, continuar e cancelar o atual.  
- Nomes de arquivos sem acentos, seguindo o motor C#.  
- Escolha da pasta pai de download e filtro de conteúdo adulto.  
**Limitações e segurança**  
- É uma **versão beta**; o funcionamento completo de todos os fluxos com contas IPTV reais não foi validado aqui.  
- A fila e os downloads em andamento não são recuperados automaticamente após reiniciar o servidor.  
- O app web ainda não reproduz todas as telas da versão Avalonia, como o player incorporado.  
- **A interface web não possui login próprio.** Não encaminhe a porta 5078 para a internet; use rede local confiável ou acesso por VPN protegida.  
- No Docker, a aplicação escuta em 0.0.0.0:5078 dentro do contêiner para que a rede do ZimaOS consiga acessá-la. Nas instalações locais pelos scripts, o endereço sugerido é localhost.  
- Não monte a raiz / do servidor no contêiner; conceda acesso apenas às pastas necessárias.  
- Utilize somente conteúdo e listas IPTV para os quais tenha direito de acesso.  
![](data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAnEAAAACCAYAAAA3pIp+AAAABmJLR0QA/wD/AP+gvaeTAAAACXBIWXMAAA7EAAAOxAGVKw4bAAAANklEQVR4nO3OQQmAABRAsScYxpg/h5VMYARvRrCCNxG2BFtmZquOAAD4i3Ot7mr/egIAwGvXA224BcUMk6pDAAAAAElFTkSuQmCC)  
**Projeto:** [lucassfontes/IPTV_Downloader ·  **Versão:** 2.0.26 Beta ·  **Backend:** C# / .NET 8 ·  **Interface:** Web](https://github.com/lucassfontes/IPTV_Downloader "https://github.com/lucassfontes/IPTV_Downloader")  
