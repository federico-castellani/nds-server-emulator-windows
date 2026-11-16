# NDS Station per Windows

Client desktop nativo per Windows 11/10 di NDS Station: accedi al tuo server, scegli un gioco dalla libreria e giochi in locale con il core di [melonDS](https://melonds.kuribo64.net). I salvataggi si sincronizzano da soli con il server.

È la riscrittura del client Qt6 ([nds-server-emulator-desktop](https://github.com/federico-castellani/nds-server-emulator-desktop)). Usa **gli stessi endpoint** del server, quindi non serve modificare il backend.

## Architettura

```
src/NdsStation/      App WinUI 3 (C#, .NET 8, Fluent Design, Mica)
  Services/          ApiClient (REST), impostazioni, archivio locale
  Emulation/         Binding P/Invoke, input, sincronizzazione salvataggi, banner ROM
  Views/             Login, Libreria, Emulatore, Amministrazione, Impostazioni
native/              ndscore.dll: API C attorno a melonDS (thread di emulazione,
                     audio SDL2 sincronizzato, controller, rete Wi-Fi)
external/melonDS     submodule, fissato al commit usato per la build
tools/               server finto e ROM homebrew di test per lo sviluppo
```

L'interfaccia è in C#; tutto ciò che dipende dalle prestazioni gira nella DLL nativa. Il thread di emulazione, cadenzato a 59,8 Hz e sincronizzato con l'audio, non dipende dal thread della UI. L'interfaccia copia solo l'ultimo frame e lo disegna con Win2D, con interpolazione nearest-neighbor.

## Funzionalità

- **Accesso**: sessione ricordata (token cifrato con DPAPI), cambio password obbligatorio al primo accesso.
- **Libreria**: griglia con ricerca. Per le ROM già in cache mostra icona e titolo estratti dal banner. Menu contestuale per rimuovere una ROM dalla cache.
- **Emulatore**:
  - disposizione schermi verticale, affiancata o "superiore grande", con scala intera opzionale;
  - schermo intero (F11), pausa (P), avanti veloce (tieni premuto Spazio o il grilletto destro);
  - 3 slot di stati salvati (F5 salva / F8 carica);
  - stilo con il mouse;
  - controller Xbox, PlayStation e Switch via SDL2;
  - tasti rimappabili.
- **Salvataggi al sicuro**:
  - ogni scrittura del gioco viene subito copiata su disco e inviata al server dopo 3 secondi senza nuove scritture;
  - uscendo, o chiudendo la finestra, l'app aspetta che il server abbia ricevuto il salvataggio; se l'invio fallisce chiede se riprovare, uscire comunque o tornare al gioco;
  - un salvataggio rimasto solo in locale viene riproposto alla partita successiva;
  - se il salvataggio del server non si scarica, il gioco non parte da vuoto, per non sovrascriverlo.
- **Amministrazione**: caricamento ROM con barra di avanzamento (il titolo viene suggerito dal banner), creazione utenti con password temporanea generata, reimpostazione password, eliminazione utenti.
- **Online**: il DNS WFC è configurabile (predefinito `178.62.43.212`). Vedi [Rete e lotte/scambi online](#rete-e-lottescambi-online).
- **Aggiornamenti**: all'avvio controlla le release di questo repository; il setup viene scaricato, verificato (SHA-256) e installato in silenzio, poi l'app si riapre. La copia portatile apre invece la pagina della release.
- **Avanti veloce con audio**: il suono resta al tono originale (la musica avanza a salti); in alternativa accelerato o muto.

## Rete e lotte/scambi online

Il Wi-Fi emulato può uscire in due modi (Impostazioni → Online → Connessione):

- **Integrata (NAT)**: nessun driver da installare. La console ha un indirizzo privato `10.64.0.x` e il traffico passa dai socket di Windows tramite libslirp.
- **Diretta (Npcap)**: la console compare sulla rete di casa con un proprio MAC e prende l'indirizzo dal router, come un DS vero. Funziona solo su **Ethernet cablata**: gli access point Wi-Fi scartano i pacchetti con un MAC diverso da quello della scheda.

**Automatica** usa la connessione diretta se [Npcap](https://npcap.com) è installato e c'è una scheda Ethernet cablata attiva, altrimenti quella integrata.

### Il problema delle lotte e degli scambi che cadevano

Nel client Qt il collegamento al server WFC funzionava, ma lotte e scambi si interrompevano all'avvio della connessione P2P. La causa, verificata da `native/tests/nettest.cpp`, è un comportamento di Windows:

1. per collegarsi direttamente, le due console si mandano pacchetti UDP "di prova" (NAT punch-through); i primi trovano spesso una porta non ancora aperta e tornano indietro come ICMP *port unreachable*;
2. su Windows quell'ICMP fa fallire la `recvfrom()` successiva con `WSAECONNRESET`;
3. libslirp (usato da melonDS per la rete integrata) traduce l'errore in un ICMP *port unreachable* inviato alla console, e lo stack di rete del DS chiude la connessione.

`ndscore` usa un proprio driver Slirp (`native/src/SlirpDriver.cpp`) che disattiva `SIO_UDP_CONNRESET` su ogni socket. Nel test, il driver di melonDS manda alla console un errore per ogni pacchetto di prova, quello di `ndscore` nessuno, e i pacchetti dell'altro giocatore arrivano comunque.

```powershell
C:\msys64\usr\bin\bash.exe -lc "cd native && cmake --build build --target nettest && PATH=/ucrt64/bin:\$PATH ./build/nettest.exe"
```

### Giocatori sulla stessa rete di casa

Quando due console raggiungono il server WFC dallo stesso IP pubblico, il gioco salta la NAT negotiation e manda all'altra console il proprio **indirizzo locale** (comando `TELL_ADDR` del matchmaking). In melonDS ogni console ha `10.64.0.16`, quindi l'altra console finiva per collegarsi a sé stessa e la lotta o lo scambio non partiva mai ("l'altro giocatore non ha risposto").

Con la rete integrata, `ndscore` dà a ogni console un indirizzo ricavato da quello del PC (PC `192.168.1.23` → console `10.168.1.23`). I pacchetti UDP diretti a un'altra console vengono inoltrati sulla rete locale al PC giusto, sulla porta **UDP 28950**, e consegnati alla sua console. Su **entrambi i PC** il Firewall di Windows deve accettare quella porta dalla rete locale: Impostazioni → Online → "Consenti nel firewall", oppure l'opzione dell'installer quando installi per tutti gli utenti. La regola si chiama `NDS Station (rete locale)` ed è limitata a UDP 28950, alla subnet locale e all'eseguibile dell'app.

Se una lotta o uno scambio si interrompe ancora, apri **Impostazioni → Log di rete**. Per ogni partita online c'è un file con i pacchetti inviati e ricevuti verso ogni indirizzo, e gli errori ICMP:
- l'altro giocatore riceve i nostri pacchetti ma da lui non torna niente: NAT del router simmetrico o CGNAT dell'operatore, oppure firewall;
- ci sono errori ICMP: c'è un problema sul percorso di rete.

I dati locali stanno in `%LOCALAPPDATA%\NDSStation`, separati per server e utente.

## Compilare

Requisiti, una sola volta:

```powershell
winget install Microsoft.DotNet.SDK.8
winget install MSYS2.MSYS2
C:\msys64\usr\bin\bash.exe -lc "pacman -S --needed --noconfirm mingw-w64-ucrt-x86_64-{gcc,cmake,ninja,pkgconf,SDL2,enet}"
git submodule update --init
```

Build (compila prima `ndscore.dll`, poi l'app):

```powershell
.\build.ps1
```

L'eseguibile è in `src\NdsStation\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\NdsStation.exe`.

Per una cartella autonoma da comprimere e distribuire, che non richiede .NET o Windows App SDK sul PC di destinazione:

```powershell
.\build.ps1 -Publish     # output in .\dist\NDSStation
```

### Installer

```powershell
winget install JRSoftware.InnoSetup   # una sola volta
.\build.ps1 -Installer                # crea .\dist\NDSStation-Setup-<versione>.exe
```

L'installer (`installer\NDSStation.iss`, circa 45 MB):
- installa per l'utente corrente senza diritti di amministratore, oppure per tutti gli utenti se lo scegli nella prima pagina;
- crea il collegamento nel menu Start e, facoltativamente, sul desktop;
- aggiorna una versione precedente rimuovendo i file vecchi;
- alla fine propone il link a Npcap, se non è installato;
- quando disinstalli chiede se cancellare anche i dati locali (risposta predefinita: no).

La versione viene presa da `<Version>` in `NdsStation.csproj`. L'eseguibile non è firmato digitalmente: al primo avvio SmartScreen mostra "Windows ha protetto il PC" (si prosegue con "Ulteriori informazioni → Esegui comunque").

## Sviluppo senza server

```powershell
python tools\mock_server.py      # http://localhost:3000, utenti admin/admin123 e mario/temp123
```

Il server finto implementa tutti gli endpoint e serve due ROM homebrew generate da `tools\make_test_rom.py`: programmi ARM minimi che colorano lo schermo superiore e hanno un'icona nel banner.

## Endpoint usati

| Metodo | Percorso | Uso |
|---|---|---|
| POST | `/auth/login` | accesso (token da cookie `auth_token` o campo `token`) |
| POST | `/auth/change-password` | cambio password |
| POST | `/auth/register` | crea utente (admin) |
| GET | `/games` | libreria |
| GET | `/roms/{id}.nds` | download ROM |
| GET / POST | `/saves?gameId=` / `/saves` | download e upload del salvataggio (multipart `gameId`, `saveFile`) |
| GET / POST | `/console` | identità della console (firmware con l'ID WFC) condivisa tra i dispositivi dell'account |
| GET | `/users/search?q=` | ricerca utenti |
| GET | `/admin/users` | elenco utenti |
| DELETE | `/admin/users/{id}` | elimina utente |
| POST | `/admin/users/{id}/reset-password` | reimposta password |
| POST | `/admin/games` | aggiunge solo il titolo |
| POST | `/games` | carica una ROM (multipart `gameName`, `ndsFile`) |

## Licenza

NDS Station è distribuito sotto licenza **GPLv3** (vedi `LICENSE`), come melonDS che include. Il setup nelle release è compilato da questo sorgente.
