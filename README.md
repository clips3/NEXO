# NEXO para AutoCAD 2027

Plugin .NET nativo para migrar ferramentas usadas nas tool palettes e rotinas AutoLISP.

## Comandos disponíveis

| Comando | Ferramenta | Ação |
| --- | --- | --- |
| `SL` | Selecionar Layer | Com pré-seleção, seleciona os objetos nas mesmas layers. Sem pré-seleção, abre uma janela para escolher uma layer e seleciona seus objetos no espaço ativo, incluindo referências de blocos com entidades nessa layer. |
| `EL2PL` | Ellipse to Poly | Converte elipses e arcos elípticos selecionados em polylines leves com segmentos curvos. |
| `NBURST` | Burst + | Explode blocos selecionados e blocos aninhados, preservando valores de atributos como texto. |
| `BLOCKREPLACERANDOM` | Auto Block | Agrupa os objetos selecionados em um bloco com nome exclusivo e ponto-base no centro geométrico. |
| `CLPV` | Clean Polyline Vertex | Executa `-OVERKILL` com otimização de segmentos das polylines selecionadas. |
| `QB` | Quebrar Polyline | Divide polylines leves selecionadas em segmentos separados, mantendo bulges, larguras e propriedades gráficas. |
| `LDL` | Deletar Layer + | Remove uma layer vazia, exclui seus objetos ou move os objetos para outra layer antes da remoção. |
| `ISOMETRICO` | Isométrico | Abre um seletor com a planta de referência fixa e projeção nas três faces visíveis do cubo. Clique em uma face para posicionar; clique novamente em passos de 90° antes de aplicar a conversão. Arcos e segmentos curvos de polylines são aproximados por 64 segmentos retos antes da transformação. Na face superior, insere também o bloco dinâmico `ISO_Laje` e ajusta X/Y ao tamanho da seleção. |
| `DET` | Detalhe | Cria uma cópia em bloco dos objetos dentro de um retângulo, recorta o bloco com XCLIP e adiciona moldura e wipeout. |
| `DETLEVE` | Detalhe Leve | Cria uma cópia do recorte, expande blocos simples, preserva referências com dados protegidos ou draw order personalizado, remove objetos totalmente fora e mantém intactos os elementos que cruzam o limite sob XCLIP. |
| `DETLEVEISO` | Detalhe Leve + Isométrico | Cria o detalhe leve e converte, em seguida, o bloco recortado, a moldura e o wipeout para a orientação de face e giro escolhidos na janela isométrica. A origem usa o centro do recorte visível, sem considerar os limites ocultos pelo XCLIP. Na face superior, insere `ISO_Laje` no centro do detalhe e ajusta X/Y às dimensões exatas do recorte. |
| `DETISOPAREDES` | Isométrico + Paredes | Reconhece paredes no recorte, mostra os candidatos para revisão e gera o detalhe isométrico da face superior com laje, paredes e aberturas. |

O comando `SL` substitui a lógica de `PP_SSLAY.lsp`. A seleção fica limitada ao espaço ativo, como exige a seleção implícita do AutoCAD. O NEXO usa o filtro nativo para localizar entidades na(s) layer(s) e percorre apenas as referências de bloco do espaço ativo; definições de bloco examinadas são armazenadas em cache durante a busca. A seleção é aplicada após o término do comando e confirmada antes de informar a contagem. Quando encontra entidades dentro de blocos, seleciona a referência inserida e percorre blocos aninhados. A janela acompanha o tema claro/escuro definido por `COLORTHEME` no AutoCAD.

O comando `EL2PL` porta a aproximação da `PP_ELL2POL.lsp` com segmentos curvos (bulges), incluindo o recorte de arcos elípticos. Como na rotina original, `DELOBJ` controla se a elipse de origem é apagada.

O comando `NBURST` porta a rotina `NBURST` de `PP_NBURST.lsp`: explode blocos aninhados, converte atributos visíveis em `TEXT`/`MTEXT`, remove conteúdo invisível e propaga layer, cor e linetype `ByBlock`. Xrefs, referências em layers bloqueadas e blocos marcados como não explodíveis são mantidos.

O comando `BLOCKREPLACERANDOM` porta a rotina `BlockReplaceRandom` de `PP_AUTOBLOCK.lsp`. O NEXO calcula a caixa envolvente, usa o centro como ponto-base, cria um nome exclusivo `RndBlk-...` e deixa os objetos agrupados em sua posição original.

O comando `CLPV` segue a macro da ferramenta CLEAN POLYLINE VERTEX (`-OVERKILL;pl;y;;`): seleciona objetos e aciona a opção `Optimize polylines = Yes` do OVERKILL. Sem pré-seleção, o comando solicita os objetos antes de executar a mesma sequência nativa.

O comando `QB` porta o resultado pretendido da `PP_BRPOL.lsp`: substitui cada polyline leve por segmentos individuais, preservando o bulge de arcos, larguras por segmento e propriedades visuais. O processamento cobre o espaço atual.

O comando `LDL` porta a rotina `ldl` de `PP_LAYDEL.lsp`. A janela acompanha o tema claro/escuro do AutoCAD e informa a quantidade de entidades em espaços e definições de bloco. Quando a layer contém objetos, é possível excluí-los com a layer ou movê-los para uma layer de destino; a layer atual não pode ser removida.

O comando `ISOMETRICO` reúne as antigas ferramentas ISOMÉTRICO DIREITA/ESQUERDA em um único botão. A planta de referência permanece fixa à esquerda; o quadrado projetado e a seta aparecem ao passar o cursor sobre uma das três faces visíveis do cubo. Clique para selecionar a face e repita o clique nela para girar a projeção em passos de 90°. No topo, `0°` e `270°` correspondem, respectivamente, aos sentidos atuais Direita e Esquerda; `90°` e `180°` completam as orientações intermediárias. Nas faces laterais, a rotação alinha a planta ao eixo diagonal da face e ao eixo vertical, com uma rotação final de `±60°` do bloco para compor a projeção. Em seguida, o comando pede uma seleção se não houver objetos pré-selecionados, transforma a geometria de acordo com a face e o giro escolhidos, agrupa a seleção em um bloco pelo centro e aplica os fatores X/Z `1.2260` e Y `0.7078`. **Somente para a face superior**, também insere o bloco dinâmico `ISO_Laje`, ajusta X e Y conforme as dimensões da seleção e o giro escolhido (ou as dimensões exatas do recorte no `DETLEVEISO`), alinha o centro da laje ao centro da conversão e envia a laje para trás do conteúdo. A espessura Z é definida em 0,15 unidade, conforme a referência. A definição dinâmica `ISO_Laje`, com parâmetros X e Y, precisa estar carregada no desenho; se estiver ausente ou não for dinâmica, o comando informa o motivo e desfaz a operação. Antes da transformação, a lógica de `ARC2SEG` (`PP_ARC2SEG.lsp`) converte arcos, círculos e trechos curvos de polylines em segmentos retos, usando 64 segmentos por arco e interpolando as larguras dos trechos de polyline. XData e propriedades gráficas são mantidas, e a ordem de desenho é restaurada. Essa sequência reúne `GIRARCENTRO` (`PP_ISO_01.lsp`), `BLOCKREPLACERANDOM` (`PP_AUTOBLOCK.lsp`) e `SBS` (`PP_ISO_02.lsp`). A janela acompanha o tema claro/escuro definido no AutoCAD.

O comando `DET` porta a criação de detalhes da `PP_DET.lsp`: seleciona objetos que cruzam uma área retangular, copia-os para o ponto escolhido, agrupa a cópia em um bloco, aplica `XCLIP`, cria contornos na layer `PP_REGIÃO` e mascara o fundo com um wipeout. A operação inteira fica em um único grupo de desfazer.

O comando `DETLEVE` segue o mesmo fluxo de posicionamento do `DET`, mas processa somente as cópias: expande blocos simples e elimina entidades completamente fora da janela. Elementos que cruzam a borda permanecem intactos e o XCLIP faz o recorte visual exato. A ordem relativa é restaurada explicitamente no conteúdo do bloco final. Blocos com atributos, propriedades dinâmicas, metadados ou draw order personalizado, além de objetos especiais e referências não explodíveis, também permanecem intactos para preservar suas informações e ordem de sobreposição; o desenho de origem não é alterado.

O comando `DETLEVEISO` executa o mesmo fluxo do `DETLEVE` e, antes de gerar qualquer cópia, abre o seletor de orientação isométrica. Após a escolha da face e do giro, aplica a transformação ao bloco recortado, à moldura e ao wipeout. Os arcos e bulges dentro da definição exclusiva do detalhe leve também são segmentados sem alterar referências compartilhadas de outros blocos. A origem da transformação é o centro do retângulo visível do detalhe. Toda a criação fica em um único grupo de desfazer.

O comando `DETISOPAREDES` inicia pelo mesmo recorte do detalhe leve e abre uma prévia de reconhecimento. A busca usa a layer de arquitetura configurada (padrão `PP_ARQUITETURA`), inclusive objetos em layer 0 dentro de blocos; encontra hachuras poligonais e pares de linhas ou segmentos de polyline retos, paralelos e próximos. Os candidatos roxos vêm de hachuras, os âmbar de linhas. Clique para incluir ou excluir trechos; continuações ambíguas identificadas somente por linhas ficam desmarcadas para revisão. Os vãos entre trechos alinhados de parede são classificados automaticamente pela largura, conforme descrito abaixo. A prévia mostra o contorno da união das paredes selecionadas e é atualizada quando a seleção muda. A layer, espessuras mínima/máxima, comprimento mínimo e altura são ajustáveis em unidades do desenho. Após a prévia, escolha o ponto central do isométrico e uma orientação da face superior. O NEXO projeta a união das áreas selecionadas e sua altura como um único bloco 2D, com contornos externos e internos, sem bordas nos encontros entre trechos; o bloco gerado não tem os parâmetros dinâmicos X/Y/Z do antigo `ISO_Parede`. A laje continua usando `ISO_Laje`, importada da biblioteca incluída caso esteja ausente. A geração inteira fica em um grupo de desfazer e as entidades da planta de origem não são modificadas.

Linhas de acabamento próximas podem ajustar a espessura de uma parede reconhecida, mas não prolongam seu comprimento para além da evidência original. Uma continuação definida somente por linhas junto a uma parede hachurada fica desmarcada para revisão, pois pode representar uma janela ou porta; ela pode ser incluída manualmente na prévia.

Na prévia, cada vão identificado entre trechos de parede é classificado somente pela largura: abaixo de `1,00` unidade do desenho é porta; `1,00` ou mais é janela. A busca considera trechos de parede alinhados, independentemente de serem derivados de hachura ou linhas. O critério não depende de arco, nome de bloco ou linha de caixilho. Portas aparecem em azul e janelas em verde; o botão direito permite trocar o tipo e ajustar as dimensões. No isométrico, o NEXO desenha as arestas visíveis da parede e sobrepõe o bloco `ISO_Porta2` ou `ISO_Janela` na posição do vão. A inserção usa o nível do peitoril e ajusta os parâmetros dinâmicos Y (largura) e Z (altura). O comando define `WIPEOUTFRAME` como `0` no desenho para ocultar as molduras das máscaras. As definições ausentes são copiadas da biblioteca `NexoOpeningBlocks.dwg`, e definições existentes são preservadas.

O reconhecimento é uma estimativa revisável, não uma interpretação BIM. Ele não reconhece paredes curvas ou diagonais nem deduz a altura de cada parede; hachuras e pares de linhas podem gerar candidatos falsos. A classificação porta/janela usa o limite fixo de `1,00` unidade do desenho, então confirme se o desenho está em metros e revise os candidatos antes de gerar. Referências com recortes não retangulares/invertidos são ignoradas. Ajuste a layer e os limites para cada padrão de desenho.

## Ribbon e ícones

Os métodos anotados com `NexoRibbonCommandAttribute` são adicionados automaticamente à aba `NEXO`. A anotação define o comando, painel, texto do botão, dica e ícone. Os PNGs ficam em `src\Nexo.AutoCAD\Resources`, são incluídos no bundle ao compilar e carregados nos tamanhos 16 e 32 com cache, seguindo o padrão do BB_PIPE. Ícones vetoriais internos também podem ser usados quando o comando precisa de um desenho próprio.

## Compilar

Requisitos: Windows x64, AutoCAD 2027 e .NET SDK 10.

```powershell
dotnet build .\Nexo.slnx -c Release
```

O build atualiza `dist\Nexo.bundle`. Para apontar para outra instalação do AutoCAD 2027:

```powershell
dotnet build .\Nexo.slnx -c Release -p:AutoCADInstallDir="C:\Program Files\Autodesk\AutoCAD 2027"
```

## Instalar

Copie `dist\Nexo.bundle` para `%APPDATA%\Autodesk\ApplicationPlugins\Nexo.bundle`. O `PackageContents.xml` registra os aliases e configura o carregamento do plugin. Para cada novo comando, adicione `NexoRibbonCommandAttribute` ao método e registre seu alias em `PackageContents.xml`.


