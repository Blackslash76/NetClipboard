<p align="center"><img src="docs/logo.png" width="120" alt="NetClipboard"></p>

<h1 align="center">NetClipboard</h1>

Clipboard condivisa **peer-to-peer sulla LAN** per Windows, con **cronologia unificata
tra tutti i tuoi device** (un "Win+V" cross-device). Supporta **testo, immagini e file**.

Tray app leggera in **C# / WinForms / .NET 10**, con un **client Android** che parla
lo stesso protocollo. Nessun server centrale.

🌐 **Sito**: https://blackslash76.github.io/NetClipboard/ · ⬇ **Download**: [Releases](https://github.com/Blackslash76/NetClipboard/releases/latest) · 📄 **Licenza**: [MIT](LICENSE)

## Come funziona

- **Identità per-dispositivo (niente password)**: ogni PC ha una coppia di chiavi
  ECDSA P-256 (privata protetta con DPAPI). L'**ID dispositivo** è l'impronta della
  chiave pubblica. La fiducia si concede una volta con un **pairing a codice** e si può
  **revocare** per singolo dispositivo. Modello stile Syncthing.
- **Scoperta** su più livelli (broadcast UDP, scansione di bootstrap, cache, gossip,
  IP manuali): l'annuncio è in chiaro, l'identità la verifica l'handshake.
- **Trasferimento (TCP)**: ogni connessione inizia con un **handshake autenticato**
  (ECDH effimero → forward secrecy) che stabilisce una chiave di sessione e un **codice
  a 6 cifre**; poi i dati viaggiano cifrati **AES-256-GCM**. Scambio dati solo tra
  dispositivi **fidati** (chiave pinnata). Il **gossip** tra fidati introduce gli altri
  → la mesh si forma da sola agganciandone uno.
- **File e cartelle in "delayed rendering"** (come Windows): copiando file/cartelle si
  condivide solo un *riferimento* (metadati: nomi, dimensioni, struttura). I byte partono
  **solo quando il destinatario incolla/materializza**, in streaming a chunk cifrati
  dall'host che possiede i file. Supporta **file multipli e cartelle annidate** (comprese
  quelle vuote).
- **Modalità ibrida**: di default ogni copia viene rispecchiata sugli altri PC; puoi
  mettere in pausa la condivisione dal menu tray (voce "Condivisione attiva").
- **Cronologia condivisa**: ogni contenuto (locale o ricevuto) entra in una history
  unificata, richiamabile con **Ctrl+Alt+V** (o doppio click sull'icona). Invio per
  incollare, tasto destro per fissare (pin) o eliminare.

> Nota su Win+V: rimpiazzare *letteralmente* la scorciatoia di sistema richiede di
> disabilitare la cronologia di Windows + un keyboard hook di basso livello. Per ora
> usiamo la nostra hotkey **Ctrl+Alt+V**; il rimpiazzo pieno è una feature successiva.

## Build

```powershell
dotnet build NetClipboard.slnx -c Release
```

L'eseguibile è in `src\NetClipboard\bin\Release\net10.0-windows\NetClipboard.exe`.

### Il client Android

```powershell
dotnet workload install android      # una volta sola, richiede privilegi di amministratore
dotnet build src\NetClipboard.Android\NetClipboard.Android.csproj -c Debug
```

Sta fuori dalla solution di proposito: senza quel carico di lavoro non si
compila, e la solution deve restare buona su una macchina Windows appena
installata.

Il protocollo **non è riscritto**: Android esegue lo stesso identico
`NetClipboard.Core` di Windows, e ciò che passa sul cavo è descritto in
[docs/PROTOCOL.md](docs/PROTOCOL.md). I valori del filo sono bloccati dal banco
`tools/conformance`, che gira in CI sia su Windows sia su Linux.

Due limiti di Android che è bene conoscere prima di provarlo:

- la clipboard si può **leggere** solo mentre l'applicazione è in primo piano
  (Android 10 e successivi). Dal PC al telefono il contenuto arriva da sé; dal
  telefono al PC si manda con un gesto;
- serve un servizio in primo piano, quindi una notifica sempre visibile: è il
  modo in cui il sistema dichiara che qualcosa sta ascoltando la rete.

## Primo avvio (su ogni PC)

1. Installa con **NetClipboard-Setup-x.y.z.exe** (installer classico, per-utente) oppure
   avvia direttamente il single-file `NetClipboard.exe` → icona nel system tray.
2. Menu tray → **"Configura firewall (admin)"** (una tantum, con UAC) se i PC non si vedono.
3. **Accoppia i dispositivi**: menu tray → **"Dispositivi e pairing…"**. Sul PC A scegli il
   PC B dall'elenco "In rete" (o inserisci il suo IP) e premi **Accoppia**. Su **entrambi**
   compare un **codice a 6 cifre**: confermalo solo se è identico sui due schermi. Da quel
   momento i due dispositivi sono fidati e condividono la clipboard.
   - Basta accoppiare un PC alla mesh: gli altri fidati vengono introdotti automaticamente.
   - Puoi **revocare** un dispositivo in qualsiasi momento dalla stessa finestra.

## Installer

La pipeline di release produce **NetClipboard-Setup-x.y.z.exe** (Inno Setup): installazione
**per macchina** in `C:\Program Files\NetClipboard` (richiede UAC una volta), collegamenti nel
menu Start, opzioni "avvia con Windows" e icona sul desktop, e disinstallazione pulita.

Da lì l'applicazione non può sostituirsi da sola, quindi l'auto-update prova prima a
scrivere accanto al proprio eseguibile e, se non può, rilancia **elevato** il binario
appena scaricato; l'app viene poi riavviata **senza privilegi**, passando da Explorer.
Un processo lanciato da uno elevato ne erediterebbe i diritti, e NetClipboard legge la
clipboard e sta in ascolto sulla rete.

## Menu tray

- **Condivisione attiva** — pausa/attiva il mirroring automatico.
- **Apri cronologia (Ctrl+Alt+V)** — popup della history cross-device.
- **Invia clipboard ora** — invio manuale on-demand ai peer.
- **Dispositivi** — elenco dei PC in linea.
- **Dispositivi e pairing…** — accoppia (codice), revoca, vedi l'impronta di questo PC.
- **Impostazioni…** — nome PC, porta, cronologia (numero/età), limite MB, tipi condivisi, aggiornamenti.
- **Configura firewall (admin)** / **Esci**.

## Aggiornamenti automatici (GitHub Releases, firmati)

L'app può aggiornarsi da **GitHub Releases**, con **firma crittografica**: accetta un
update solo se firmato con la chiave privata release (la pubblica è incorporata
nell'app). Così nessuno può iniettare un finto aggiornamento, anche se il canale
fosse compromesso. L'installazione è **con conferma**: l'app scarica e verifica in
background, poi avvisa e aspetta che tu scelga "Installa aggiornamento e riavvia".

Configurazione (una volta): in **Impostazioni → URL aggiornamenti** metti
`https://github.com/UTENTE/REPO/releases/latest/download/manifest.json`.

### Pubblicare una nuova versione

1. Alza `<Version>` in `Directory.Build.props` (da lì derivano l'eseguibile, il manifest e il `versionCode` dell'APK) e ripubblica il single-file.
2. Firma la release (la chiave privata è in `.vault`, mai condivisa):
   ```powershell
   NetClipboard.exe --sign-release publish\NetClipboard.exe 1.1.0 `
     .vault\release-signing.key `
     https://github.com/UTENTE/REPO/releases/latest/download/NetClipboard.exe `
     manifest.json "Novità 1.1.0"
   ```
3. Crea la Release su GitHub (tag `v1.1.0`) e allega **`NetClipboard.exe`** e **`manifest.json`**.

I client vedono la nuova versione entro poche ore (o subito con "Controlla aggiornamenti").

> I segreti (chiave privata di firma) stanno in `.vault/`, esclusa da git. Non
> condividerla: chi la possiede può firmare aggiornamenti accettati dai client.

## Dove sono i dati

`%AppData%\NetClipboard\`
- `config.json` — configurazione. `identity.key` — chiave privata del dispositivo (DPAPI).
  `trusted.json` — dispositivi fidati (chiavi pubbliche pinnate).
- `history\` — indice cronologia + immagini.
- `received\` — file ricevuti dai peer.

## File e cartelle: come si incolla (v1)

Copiando file/cartelle il contenuto **non** viene inviato subito. Sul PC di destinazione:
apri la cronologia con **Ctrl+Alt+V**, seleziona la voce (marcata "da scaricare") →
i byte vengono scaricati dall'host in quel momento e messi in clipboard, **pronti da
incollare** con un normale Ctrl+V in Esplora file. Se l'host è offline compare un avviso.

> Prossimo passo: **Ctrl+V nativo** direttamente in Esplora (oggetto clipboard COM con
> rendering ritardato), così non serve passare dal popup.

## Limiti noti (v1)

- Testo e immagini viaggiano per valore, con limite configurabile (default 50 MB).
- I file usano il trasferimento on-demand (nessun limite pratico): l'host li streama a
  chunk da 64 KB solo su richiesta.
- **Scoperta dei peer** su più livelli, robusta anche quando la rete filtra il broadcast
  (Wi-Fi con isolamento client, ecc.):
  1. **Broadcast UDP** — istantaneo sulle reti normali.
  2. **Scansione TCP della subnet** — solo al **primo avvio** (bootstrap): prova a
     "bussare" a ogni IP della subnet sulla porta dedicata. Dopo, mai più in automatico.
  3. **Cache dei peer noti** — gli IP già visti vengono salvati e ripingati al riavvio
     (niente nuova scansione).
  4. **Gossip / mesh auto-assemblante** — a ogni handshake i nodi si scambiano la lista
     dei peer conosciuti: appena ti agganci a *un* nodo di una rete esistente, scopri
     automaticamente tutti gli altri (e loro te).
  5. **Pulsante "Cerca dispositivi in rete"** — scansione on-demand quando serve.
  6. **IP peer manuali** (Impostazioni) — fallback esplicito; reciproco, basta che un
     solo PC inserisca l'IP dell'altro.

  Nota: la scansione della subnet può far scattare i sistemi di sicurezza di reti
  aziendali (sembra un port-scan): per questo è limitata al bootstrap + on-demand, e
  disattivabile da Impostazioni ("Scoperta automatica"). Gli IP sono statici: con DHCP
  conviene riservarli sul router.
- I metadati di cartelle molto grandi vengono calcolati al momento della copia sul thread
  UI: con decine di migliaia di file la copia può richiedere un attimo (cap a 50k voci).

## Struttura del codice

Una regola sola, e da lì discende tutto il resto: **quello che passa sulla rete
sta in `NetClipboard.Core`, e non contiene niente di specifico a un sistema
operativo.** Ciò che il sistema deve fornire — dove si custodisce una chiave, chi
giudica un contenuto in arrivo — entra da un'interfaccia, e la piattaforma la
implementa.

```
Directory.Build.props           la VERSIONE, una sola per tutti i prodotti

src/NetClipboard.Core/          net10.0 · lo stesso codice su ogni piattaforma
├─ AppConfig.cs                 configurazione JSON
├─ Resources/it.json            i testi, uno per tutte le applicazioni
├─ Core/
│  ├─ ClipboardPayload.cs       modello e (de)serializzazione binaria
│  ├─ FileOffer.cs              offerta di file (solo metadati)
│  ├─ ClipboardHistory.cs       cronologia cifrata a riposo
│  ├─ CfHtml.cs                 il formato HTML della clipboard
│  └─ Security/
│     ├─ DeviceIdentity.cs      identità ECDSA P-256
│     ├─ Handshaker.cs          handshake autenticato + SAS
│     ├─ SessionCipher.cs       AES-256-GCM di sessione
│     ├─ TrustStore.cs          chiavi pinnate e revoche
│     ├─ ISecretProtector.cs    dove il sistema custodisce un segreto
│     └─ IContentScanner.cs     chi giudica un contenuto in arrivo
└─ Net/
   ├─ ClipboardTransport.cs     servente e cliente TCP, gossip, offerte
   └─ PeerDiscovery.cs          annunci UDP

src/NetClipboard/               net10.0-windows · WinForms
├─ Program.cs                   avvio, istanza unica, --install-firewall
├─ Platform/WindowsPlatform.cs  DPAPI e AMSI dietro le interfacce del core
├─ Core/ClipboardMonitor.cs     WM_CLIPBOARDUPDATE, scorciatoia, anti-eco
├─ Core/Security/               AMSI, stato dell'antivirus di sistema
├─ Net/FirewallHelper.cs        regola del firewall via UAC
├─ Update/Updater.cs            aggiornamento firmato
└─ Ui/                          tray, cronologia, impostazioni, dispositivi

src/NetClipboard.Android/       net10.0-android · Avalonia
├─ Platform/                    portachiavi di sistema, clipboard, conferme
├─ Services/                    il servizio in primo piano che tiene l'ascolto
└─ Views/MainView.cs            l'unica schermata

tools/                          fuori dalla solution, fuori dal rilascio
├─ conformance/                 i valori del filo, bloccati in vectors.json
├─ selftest/                    parser di rete sotto dati ostili
├─ e2e/                         nove istanze del trasporto su loopback
├─ uishot/                      fotografa le finestre nei due temi
└─ check-strings.ps1            i cataloghi contro l'uso reale
```
