namespace NetClipboard.Core.Security;

/// <summary>
/// Dove il sistema operativo custodisce un segreto dell'applicazione: la chiave
/// di identita' del dispositivo e la chiave della cronologia.
///
/// Su Windows e' DPAPI (ambito utente corrente); su Android il portachiavi di
/// sistema. Sono cose diverse e non intercambiabili, ma la promessa e' la
/// stessa: quei byte restano leggibili solo a questo utente su questo
/// dispositivo, e chi copia via il profilo non se li porta appresso.
///
/// <para>
/// NON esiste un'implementazione di riserva che restituisce i byte com'e'.
/// Sarebbe la piu' comoda da scrivere e la piu' facile da dimenticare attiva:
/// il risultato sarebbe la chiave privata del dispositivo, in chiaro, su disco,
/// senza che niente lo segnali. Se una piattaforma non registra un protettore,
/// il core preferisce non ricordare nulla — un'identita' che non si salva si
/// nota subito (non si resta accoppiati), una chiave in chiaro no.
/// </para>
/// </summary>
public interface ISecretProtector
{
    /// <summary>Avvolge dei byte perche' possano stare su disco.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>
    /// Riapre cio' che <see cref="Protect"/> aveva avvolto, oppure null se non e'
    /// possibile (chiave di sistema cambiata, profilo diverso, file rovinato).
    /// </summary>
    byte[]? Unprotect(byte[] wrapped);
}
