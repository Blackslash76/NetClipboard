using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace NetClipboard.Core.Identity;

/// <summary>
/// Accesso a Microsoft Entra ID come "public client" desktop.
///
/// Punti chiave:
///  - si usa il broker di Windows (WAM): su un PC aggiunto al dominio Entra
///    l'utente è già autenticato, quindi il token arriva in silenzio, senza
///    nessuna finestra di login e senza gestire password;
///  - la cache dei token è quella del sistema operativo (la tiene WAM), perciò
///    il silenzioso continua a funzionare dopo il riavvio senza che l'app
///    scriva token su disco;
///  - l'unico permesso richiesto è User.Read, delegato: lo concede il singolo
///    utente, non serve l'approvazione dell'amministratore del tenant.
///
/// L'app resta perfettamente funzionante senza tutto questo: se il ClientId non
/// è configurato, <see cref="IsConfigured"/> è false e il resto del programma
/// prosegue con la sola identità di dispositivo.
/// </summary>
public sealed class EntraAuth
{
    /// <summary>Permesso minimo: leggere il profilo di chi ha fatto l'accesso.</summary>
    private static readonly string[] Scopes = { "https://graph.microsoft.com/User.Read" };

    /// <summary>Qualsiasi account aziendale o dell'istituto (esclude gli account Microsoft personali).</summary>
    public const string AnyOrganization = "organizations";

    private readonly string _clientId;
    private readonly string _tenant;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IPublicClientApplication? _app;

    /// <summary>Identità dell'utente attualmente autenticato, o null se non c'è.</summary>
    public WorkIdentity? Current { get; private set; }

    /// <summary>
    /// Ultimo ID token grezzo ricevuto: è la prova firmata da Entra dell'identità
    /// dell'utente, da presentare al peer durante il pairing.
    /// </summary>
    public string? CurrentIdToken { get; private set; }

    /// <summary>Notifica i cambi di stato dell'accesso (login riuscito, logout).</summary>
    public event Action<WorkIdentity?>? Changed;

    /// <summary>
    /// Finestra a cui agganciare il popup di login. In WinForms va impostata con
    /// l'handle del form che avvia l'accesso; il default segue la finestra in
    /// primo piano, che è la scelta giusta quando la richiesta parte dalla tray.
    /// </summary>
    public Func<IntPtr> ParentWindow { get; set; } = GetForegroundWindow;

    public EntraAuth(string clientId, string? tenant = null)
    {
        _clientId = (clientId ?? "").Trim();
        _tenant = string.IsNullOrWhiteSpace(tenant) ? AnyOrganization : tenant.Trim();
    }

    /// <summary>False finché non è stata registrata un'app nel tenant e messo il suo ClientId in configurazione.</summary>
    public bool IsConfigured => Guid.TryParse(_clientId, out _);

    /// <summary>
    /// Prova a ottenere l'identità senza mostrare nulla all'utente. È il percorso
    /// normale all'avvio: su un PC Entra-joined riesce sempre.
    /// Restituisce null se serve un accesso esplicito (vedi <see cref="SignInInteractiveAsync"/>).
    /// </summary>
    public async Task<WorkIdentity?> SignInSilentAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var app = Build();

            // Prima gli account che MSAL già conosce, poi quello con cui è
            // avviata la sessione di Windows: copre sia il ri-accesso sia il
            // primo avvio su una macchina aggiunta al tenant.
            var accounts = (await app.GetAccountsAsync().ConfigureAwait(false)).ToList();
            accounts.Add(PublicClientApplication.OperatingSystemAccount);

            foreach (var account in accounts)
            {
                try
                {
                    var result = await app.AcquireTokenSilent(Scopes, account)
                        .ExecuteAsync(ct).ConfigureAwait(false);
                    return Apply(result);
                }
                catch (MsalUiRequiredException)
                {
                    // questo account non basta da solo: si prova il prossimo
                }
            }
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Write($"Entra: accesso silenzioso non riuscito: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Accesso con interazione dell'utente (finestra del broker di Windows).
    /// Da chiamare solo su azione esplicita, mai all'avvio.
    /// </summary>
    public async Task<WorkIdentity?> SignInInteractiveAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var app = Build();
            var result = await app.AcquireTokenInteractive(Scopes)
                .WithParentActivityOrWindow(ParentWindow())
                .ExecuteAsync(ct).ConfigureAwait(false);
            return Apply(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            Log.Write("Entra: accesso annullato dall'utente.");
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"Entra: accesso non riuscito: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Dimentica l'utente: rimuove gli account dalla cache e azzera lo stato.</summary>
    public async Task SignOutAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app != null)
                foreach (var account in await _app.GetAccountsAsync().ConfigureAwait(false))
                    try { await _app.RemoveAsync(account).ConfigureAwait(false); } catch { }
        }
        catch (Exception ex)
        {
            Log.Write($"Entra: disconnessione parziale: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }

        Current = null;
        CurrentIdToken = null;
        Changed?.Invoke(null);
    }

    private WorkIdentity? Apply(AuthenticationResult result)
    {
        var identity = WorkIdentity.FromIdToken(result.IdToken);
        if (identity == null)
        {
            Log.Write("Entra: token ottenuto ma privo delle claim tid/oid.");
            return null;
        }

        Current = identity;
        CurrentIdToken = result.IdToken;
        Log.Write($"Entra: autenticato come {identity}");
        Changed?.Invoke(identity);
        return identity;
    }

    private IPublicClientApplication Build() =>
        _app ??= PublicClientApplicationBuilder
            .Create(_clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _tenant)
            .WithDefaultRedirectUri()
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
            .Build();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
