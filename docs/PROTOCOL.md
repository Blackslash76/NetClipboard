# Il protocollo di NetClipboard

Versione del filo: **2**. Questo documento descrive cosa passa davvero sul cavo,
byte per byte. Serve a due cose: rileggere una decisione senza doverla ricavare
dal codice, e permettere a un'implementazione che non sia in .NET di parlare con
le altre senza indovinare.

L'implementazione di riferimento è `src/NetClipboard.Core`, ed è la stessa su
ogni piattaforma. I valori qui descritti sono **bloccati** dal banco di
conformità (`tools/conformance`): se cambiano senza che qualcuno lo dichiari, la
pipeline si ferma.

---

## 0. Convenzioni di codifica

Il codice usa `BinaryWriter`/`BinaryReader`, e questo fissa due cose che è facile
sbagliare a reimplementare:

| cosa | come |
|---|---|
| interi dentro i messaggi (`int32`, `int64`) | **little-endian** |
| lunghezza del frame (i 4 byte davanti a ogni frame) | **big-endian** |
| booleani | 1 byte, `0` o `1` |
| stringhe | `[lunghezza in byte: int32][UTF-8]`, **senza** terminatore |
| GUID | i 16 byte di `Guid.ToByteArray()`: primi tre campi little-endian, ultimi otto in ordine — **non** è la forma RFC 4122 |

Sì, la lunghezza del frame ha l'ordine opposto a tutto il resto. È così dalla
prima versione e cambiarlo romperebbe ogni installazione esistente: si scrive
com'è.

Ogni campo con un prefisso di lunghezza è limitato a **64 KB**. Chi legge deve
confrontare la lunghezza dichiarata con lo spazio realmente disponibile *prima*
di allocare: dichiarare 600 MB in un messaggio da nove byte è un attacco che
riesce, e riesce anche a chi non è accoppiato.

---

## 1. Scoperta — UDP, in chiaro

Porta: la stessa del trasporto (`45654` di default). Ogni **3 secondi** si manda
un annuncio a `255.255.255.255` e all'indirizzo di broadcast di ogni interfaccia
IPv4 attiva.

```
[ 'N' ][ 'C' ][ 0x02 ][ porta TCP : int32 LE ]
```

Chi riceve un annuncio da un indirizzo che non è suo aggiunge quell'IP fra i
candidati da contattare. **Nient'altro.** L'annuncio non dice chi sei e non va
creduto: l'identità nasce dall'handshake TCP, che è autenticato. Un annuncio
falsificato ottiene, al massimo, che qualcuno provi a connettersi.

Su Android la ricezione dei pacchetti di broadcast richiede un
`WifiManager.MulticastLock`: senza, il sistema li scarta prima che arrivino
all'applicazione, e la scoperta funziona solo nel verso in uscita.

---

## 2. Trasporto — TCP

Ogni operazione è **una connessione**: ci si connette, si fa l'handshake, si
manda un'operazione, eventualmente si legge la risposta, si chiude. Non ci sono
connessioni persistenti da tenere vive, ed è la ragione per cui un telefono che
va in sospensione non lascia niente di appeso.

### 2.1 Framing

```
[ lunghezza : int32 BIG-endian ][ contenuto ]
```

Durante l'handshake un frame non può superare **64 KB**: lì passano solo chiavi e
firme, e finché non si sa chi c'è dall'altra parte il tetto dei dati (decine di
MB) significherebbe far allocare decine di MB a chiunque sia in rete. Dopo
l'handshake il tetto è `MaxTransferMb + 4 MB`.

Un servente accetta al massimo **32 sessioni insieme**; oltre, rifiuta la
connessione.

### 2.2 Handshake — Station-to-Station

Tre frame **in chiaro**, poi tutto è cifrato.

```
C → S : [ 'N' ][ 'C' ][ 0x02 ][ idPubC ][ ephPubC ]
S → C : [ idPubS ][ ephPubS ][ firmaS ]
C → S : [ firmaC ]
```

Ogni chiave e ogni firma è un campo con prefisso di lunghezza.

- **idPub** — chiave pubblica ECDSA P-256 in formato SubjectPublicKeyInfo (DER).
  È l'identità del dispositivo, stabile nel tempo.
- **ephPub** — chiave pubblica ECDH P-256 (SPKI DER), **nuova a ogni
  connessione**: è ciò che dà la forward secrecy. Riusarla la annulla.

Il **transcript** è ciò che entrambi firmano:

```
transcript = SHA-256(
    [len][idPubInit] [len][idPubResp] [len][ephPubInit] [len][ephPubResp]
)                                     (len = int32 little-endian)
```

`Init` è chi ha aperto la connessione, `Resp` chi l'ha accettata. L'ordine deve
coincidere sui due lati, o i transcript non combaciano e non combacia niente.

Il segreto condiviso è **`SHA-256` della coordinata X** del punto ECDH — non la
coordinata nuda. (In .NET è `DeriveKeyFromHash(peer, SHA256)`; altrove va scritto
a mano, ed è l'unico punto in cui una libreria diversa sbaglia in silenzio.)

Da lì, due derivazioni HKDF-SHA256 con il transcript come *salt*:

```
chiave di sessione = HKDF(ikm = segreto, salt = transcript, info = "netclip-session-v1", 32 byte)
sas                = HKDF(ikm = segreto, salt = transcript, info = "netclip-sas-v1",      4 byte)
                     → uint32 little-endian % 1 000 000, scritto a 6 cifre con gli zeri davanti
```

Le firme sono **ECDSA/SHA-256 sul transcript**. Chi non verifica la firma
dell'altro chiude la connessione: senza quella verifica l'handshake sarebbe
autenticato solo dal SAS, e il SAS lo si guarda una volta sola, al pairing.

L'**ID dispositivo** è `SHA-256(idPub)` in esadecimale maiuscolo (64 caratteri).
Non è dichiarato da nessuno: si calcola, e per questo non si può mentire.

### 2.3 Cifratura di sessione

Dopo l'handshake ogni frame è:

```
[ lunghezza : int32 BE ][ nonce : 12 ][ tag : 16 ][ ciphertext ]
```

AES-256-GCM con la chiave di sessione, nonce casuale per frame, nessun dato
aggiuntivo autenticato. Un frame che non si apre si scarta e la connessione
finisce lì.

Lo stesso formato — meno il prefisso di lunghezza, più la firma `NCV1` in testa —
è quello con cui la cronologia sta cifrata sul disco.

---

## 3. Le operazioni

Il **primo** frame cifrato comincia con un byte di operazione.

| byte | operazione | chi può |
|---|---|---|
| `1` | **Ping** — presenza e gossip | chiunque |
| `2` | **Push** — contenuto verso i propri dispositivi | solo fidati |
| `3` | **Fetch** — prelievo dei file di un'offerta | fidati, o chi ha un permesso per *quella* offerta |
| `4` | **Pair** — accoppiamento | chiunque (l'utente conferma) |
| `5` | **Offer** — invio mirato a un non accoppiato | chiunque (l'utente conferma) |

### 3.1 Ping (`1`)

Corpo, e identico nella risposta (che è preceduta di nuovo dal byte `1`):

```
[ nome : stringa ]
[ porta : int32 ]
[ numero di presentati : int32 ]
   ripetuto: [ deviceId : stringa ][ nome : stringa ][ ip : stringa ][ porta : int32 ][ chiave pubblica : campo ]
[ tenantId : stringa ][ objectId : stringa ][ userPrincipalName : stringa ][ nome visualizzato : stringa ]
```

I presentati (**gossip**) si mandano solo ai fidati e si leggono solo da un
fidato: sono i dispositivi che diciamo di conoscere, al massimo 64.

**Ricevere una presentazione non fa entrare nessuno.** Fino alla 2.6.2 bastava:
un solo dispositivo accoppiato finito in mano a qualcun altro faceva entrare la
sua chiave nella cerchia di tutti, in silenzio. Ora è una proposta, e decide
l'utente. Un rifiuto è una lapide persistente: non si ripropone, e si azzera solo
con un pairing esplicito.

I quattro campi finali sono l'**identità aziendale dichiarata**, ed è appunto
dichiarata: chiunque può scriverci un nome altrui. Servono a rendere leggibile un
elenco, non a decidere di chi fidarsi.

Quei campi sono in **coda** di proposito: una versione precedente legge fino al
gossip e si ferma senza guardare se il buffer è finito. È la regola generale del
formato, e vale per chiunque voglia aggiungere qualcosa:

> Un campo aggiunto in coda è retrocompatibile finché chi legge si ferma senza
> controllare la fine del buffer. Chi aggiunge una coda deve accettare che una
> **sconosciuta** venga saltata, o la prossima aggiunta romperà questa versione.

### 3.2 Push (`2`)

`[2][ payload ]` (§4). Chi riceve **deve** verificare che il mittente sia fidato
prima di deserializzare. Nessuna risposta.

### 3.3 Fetch (`3`)

`[3][ offerId : 16 byte ]`. Se il richiedente è fidato, oppure ha un permesso
valido per quella offerta, si risponde con una successione di frame:

| tag | frame |
|---|---|
| `0x01` | `[isDir : 1][dimensione : int64][lunghezza nome : int32][percorso relativo UTF-8]` |
| `0x02` | `[byte del file]` (al massimo 64 KB per frame) |
| `0x03` | fine della voce corrente |
| `0x00` | fine della trasmissione |
| `0x7F` | `[lunghezza : int32][codice UTF-8]` — errore |

L'errore viaggia come **codice**, non come frase: la traduce chi legge, nella
propria lingua. Oggi esiste `offer-gone`.

Il percorso relativo arriva dal mittente ed è **dato ostile**. Chi scrive deve
rifiutare percorsi assoluti, risalite (`..`), due punti (unità e stream
alternativi NTFS), segmenti che finiscono con spazio o punto, e verificare che il
percorso normalizzato resti dentro la cartella di destinazione. Senza questi
controlli un mittente accoppiato scrive dove vuole — per esempio nella cartella
di esecuzione automatica.

### 3.4 Pair (`4`)

```
C → S : [4][ nome : stringa ][ porta : int32 ][ 0 : int32 ]
```

Poi entrambi mostrano all'utente il **SAS** derivato dall'handshake e si scambiano
un byte con la propria risposta (`1` sì, `0` no). Si diventa fidati **solo se
entrambi** hanno detto sì.

Chi implementa deve **mettersi in ascolto della risposta dell'altro subito**, non
dopo aver risposto: se arriva uno `0` mentre la propria domanda è ancora a
schermo, quella domanda va chiusa. Altrimenti chi annulla lascia l'altro davanti
a una finestra che chiede di confrontare un codice ormai morto, fino alla
scadenza — e su due dispositivi diversi sembra un guasto. Non c'è rischio di
stallo: la scrittura della propria risposta non dipende dalla lettura.

Vale anche per una connessione che cade: l'assenza di risposta è un no.

Fidarsi significa memorizzare `deviceId → chiave pubblica`. Da quel momento la
chiave è **pinnata**: se un giorno la stessa identità si presentasse con una
chiave diversa, non è la stessa identità.

### 3.5 Offer (`5`)

`[5][ payload ]`, verso un dispositivo **non** accoppiato. Chi riceve mostra la
richiesta e risponde con un byte (`1` accettato, `0` no).

Difese obbligatorie in ricezione, perché qui parla chiunque:

- **una richiesta ogni 10 secondi per mittente**, e mai due finestre insieme;
- testo e immagini si analizzano **prima** di mostrare la richiesta: un contenuto
  riconosciuto come dannoso non deve nemmeno proporsi;
- se il contenuto sono file, accettare concede un permesso di prelievo per
  **quella** offerta e per **tre minuti**. La scadenza è un istante assoluto UTC:
  un contatore da avvio (`TickCount`) riparte da zero proprio quando serve
  rileggerlo.

---

## 4. Il payload

```
[ tipo : 1 ]
  1 = Testo   → [ lunghezza : int32 ][ UTF-8 ]  poi code facoltative
  2 = Immagine→ [ lunghezza : int32 ][ PNG ]
  3 = File    → offerta (§4.1)
```

Le **code** del testo, ripetibili e in qualunque numero:

```
[ etichetta : 1 ][ lunghezza : int32 ][ UTF-8 ]
   1 = frammento HTML (senza intestazione CF_HTML)
   2 = RTF
```

Si legge finché ci sono byte; chi non riconosce un'etichetta **salta la coda e
prosegue**. Tetto: **2 MB** per coda — oltre, la formattazione si lascia cadere e
resta il testo, che è ciò che serviva.

Dell'HTML viaggia **solo il frammento**. L'intestazione CF_HTML la ricostruisce
chi riceve, così i suoi scarti sono per forza giusti e non si porta in giro il
campo `SourceURL`, che spesso è un percorso locale di chi ha copiato. Gli scarti
del CF_HTML si misurano **in byte sull'UTF-8**, non in caratteri: una sola
lettera accentata prima del frammento sposta tutto di uno.

L'**impronta del contenuto** (deduplica, anti-eco) per il testo guarda **solo il
testo**, non la formattazione: lo stesso paragrafo copiato da due programmi è la
stessa cosa per chi guarda l'elenco, e l'HTML non torna indietro identico al byte
dalla clipboard — bastava quello per riaprire il ping-pong.

### 4.1 Offerta di file

I byte non viaggiano col payload: viaggia l'elenco, e i byte si chiedono dopo con
Fetch. È ciò che permette di "copiare" venti gigabyte senza mandarli a nessuno.

```
[ offerId : 16 byte ]
[ deviceId del proprietario : stringa ]
[ nome del proprietario : stringa ]
[ numero di voci : int32 ]
   ripetuto: [ indice radice : int32 ][ è cartella : 1 ][ dimensione : int64 ][ percorso relativo : stringa ]
[ miniatura : campo ]                      ← facoltativa, in coda
[ numero di date : int32 ]                 ← facoltativa, in coda
   ripetuto: [ modifica, ms Unix UTC : int64 ]
```

Al massimo **50 000** voci, e comunque non più di quante ne stiano nei byte
rimasti (ogni voce ne occupa almeno 17).

La **miniatura** è un JPEG o un PNG piccolo, al massimo **64 KB**, e serve a
mostrare *cosa* si sta per scaricare: nel rendering ritardato chi riceve ha solo
nomi e dimensioni, e di una foto vedrebbe soltanto il nome. È in coda per la
regola del §"come si cambia il protocollo": un mittente di versione precedente
non la manda affatto, e un lettore di versione precedente si ferma dopo le voci
senza accorgersene. Chi legge deve accettare **entrambi** i casi — l'assenza non
è un errore — e ignorare una miniatura più grande del tetto invece di scartare
l'offerta.

> **La fornisce chi manda.** È quindi un'immagine di provenienza esterna che il
> destinatario decodifica: un decodificatore di immagini è una superficie
> d'attacco. Si disegna solo per i mittenti **fidati**, mai nella richiesta di
> conferma di un invio da un non accoppiato — che è esattamente il momento in cui
> non ci si fida.

Le **date di modifica** sono una per voce, nello stesso ordine delle voci, in
millisecondi dall'epoca Unix (UTC). Millisecondi e non tick di .NET perché questo
è un formato di filo, e deve poterlo scrivere anche chi non programma in .NET.

Si scrivono **solo se almeno una è nota**: un'offerta tutta senza date non porta
la coda affatto, e resta byte per byte quella di prima. Zero significa *non nota*
— mittente di versione precedente, o file la cui data non si è potuta leggere —
e non deve essere trattato come una data reale. Chi legge accetta che il numero
di date non coincida con quello delle voci: in quel caso non sono le sue, e le
ignora tutte.

> **A cosa servono.** Non a mostrare una data: all'**impronta del contenuto**.
> L'impronta di un'offerta non può guardare i byte — i byte non sono ancora
> viaggiati, è tutto il senso del prelievo differito — quindi guarda i metadati.
> Con soli percorso e dimensione, due file diversi ma della stessa misura
> risultano lo stesso contenuto: la cronologia li fonde in una voce sola, e chi
> incolla rischia di vedersi servire i byte sbagliati. Con la data si
> distinguono. Resta un'approssimazione, e va detto: è il meglio che si può fare
> senza trasferire i byte.

**Chi salta una coda deve smettere di leggere.** Se una coda si ignora senza
consumarne i byte — per esempio una miniatura oltre il tetto — il flusso resta
disallineato, e tutto ciò che segue verrebbe letto storto. Da quel punto in poi
non si legge più niente.

---

## 5. Cosa deve fare un'implementazione per essere corretta

Non è un elenco di buone intenzioni: sono le proprietà da cui dipende tutto il
resto.

1. **Verificare la firma sul transcript** dei due lati. Senza, l'handshake non
   autentica niente.
2. **Pinnare la chiave pubblica** al pairing e confrontarla a ogni connessione.
   Il `deviceId` da solo non basta: è un'impronta, e va ricalcolata dalla chiave
   presentata.
3. **Push e Fetch solo verso e da chi è fidato** (o chi ha un permesso puntuale,
   per Fetch).
4. **Non fidarsi mai per gossip.** Una presentazione è una proposta da mostrare
   all'utente.
5. **Confrontare ogni lunghezza dichiarata con lo spazio disponibile** prima di
   allocare.
6. **Rifiutare percorsi non sicuri** nei file in arrivo.
7. **Non propagare un contenuto marcato come riservato** dal sistema (i gestori
   di password lo dichiarano; su Windows con i formati `Clipboard Viewer Ignore`,
   `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory`,
   `CanUploadToCloudClipboard`). Nel dubbio si tace: perdere una copia è una
   seccatura, spargere una password no.
8. **Custodire la chiave privata** dove il sistema custodisce i segreti, mai in
   chiaro su disco.

---

## 6. Come si cambia il protocollo

1. Aggiungere **in coda**, mai in mezzo, e accettare che le code sconosciute si
   saltino.
2. Far girare `tools/conformance`: si fermerà. Guardare cosa è cambiato.
3. Registrare con `dotnet run --project tools/conformance -- --record` e mettere
   la differenza di `vectors.json` sotto gli occhi di chi rivede il codice. Quel
   file è fatto per essere letto in una differenza.
4. Aggiornare questo documento nello stesso commit.
5. Alzare il numero di versione del filo **solo** se un vecchio non può più
   parlare con un nuovo. Fino a oggi non è mai stato necessario, ed è il segno
   che le regole sopra funzionano.
