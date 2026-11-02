# P2P Call — cliente WPF

Cliente desktop (WPF / .NET 9) para chamadas de voz P2P com compartilhamento de tela.
Usa o mesmo protocolo de sinalização Socket.IO do cliente web (`join`, `user-joined`,
`offer`, `answer`, `ice-candidate`, `user-disconnected`), então dá para misturar
participantes web e desktop na mesma sala.

## Recursos

- Áudio P2P via WebRTC ([SIPSorcery](https://github.com/sipsorcery-org/sipsorcery)), Opus estéreo/FEC ([Concentus](https://github.com/lostromb/concentus))
- Captura e reprodução com [NAudio](https://github.com/naudio/NAudio), sem eco/ruído/AGC (áudio cru, como no web)
- Seleção de microfone (troca durante a chamada) e qualidade de envio (32–510 kbps)
- Silenciar microfone
- Gravação da chamada (microfone + participantes) em WAV, salva em `Músicas\P2PCall`
- **Compartilhar tela**: tela principal em VP8 (até 1600 px, 12 fps, com cursor); as telas
  recebidas aparecem em grade e somem 3 s depois que o participante para de compartilhar
- Mantém o PC acordado durante a chamada (equivalente ao Wake Lock)

> Participantes web só negociam áudio — eles falam e ouvem normalmente, mas não recebem a tela.
> A resposta SDP do cliente desktop espelha exatamente as m-lines da oferta (o navegador rejeita
> m-lines extras), e renegociações do web (ex.: "Compartilhar Áudio do PC") reaproveitam a conexão,
> com um decodificador por trilha de áudio. Entre clientes desktop a tela funciona sem renegociação,
> pois a trilha de vídeo é criada junto com a conexão.

## Acessibilidade

Pensado para uso com leitor de telas (Narrador, NVDA, JAWS) e só com teclado:

- Todo campo tem rótulo associado, nome acessível e dica (`HelpText`); "Sala" é marcado como obrigatório
- Teclas de acesso: **Alt+V** servidor, **Alt+S** sala, **Alt+C** microfone, **Alt+Q** qualidade,
  **Alt+E** entrar, **Alt+M** microfone, **Alt+T** tela, **Alt+G** gravar, **Alt+R** sair
- **Ctrl+M** silencia/ativa o microfone de qualquer lugar da janela
- Silenciar, compartilhar tela e gravar são botões de alternância: o leitor anuncia
  "pressionado"/"não pressionado" (como o `aria-pressed` do web) e funcionam pelo padrão Toggle da automação
- Status e número de participantes são regiões "ao vivo": mudanças são anunciadas sem mover o foco
  (inclui mute, início/fim de compartilhamento de tela de outros participantes, gravação salva)
- O foco nunca se perde: vai para o campo com erro, para "Silenciar microfone" ao entrar e para
  "Entrar na sala" ao sair
- Anel de foco bem visível e contraste de texto acima de 4,5:1

## Executar

O servidor padrão é `https://voz.tempsharing.com.br`. Para testar localmente, use o servidor mínimo incluso em `server/`:

```bash
cd server && npm install && npm start
```

Cliente:

```bash
dotnet run --project src/P2PCall.Client -- --sala minhaSala
```

`--sala` e `--server <url>` são opcionais (equivalentes ao `?sala=` da versão web).

## Estrutura

| Pasta | Conteúdo |
| --- | --- |
| `src/P2PCall.Client/Audio` | `AudioEngine`: microfone → Opus, mixagem das vozes remotas, gravação |
| `src/P2PCall.Client/Call` | `CallManager` (sinalização + RTCPeerConnection por participante), `PeerSession` |
| `src/P2PCall.Client/Controls` | Controles que evitam leitura duplicada do texto com tecla de acesso |
| `src/P2PCall.Client/Video` | `ScreenSharer` (captura GDI → VP8), `RemoteScreen` (exibição) |
| `server/` | Servidor Socket.IO mínimo para testes |
