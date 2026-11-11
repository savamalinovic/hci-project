# Paperless Desktop

Windows desktop klijent za Paperless-ngx, napravljen u C# i WPF-u.

## Pokretanje iz izvornog koda

Potreban je Windows i .NET 8 SDK. Iz osnovnog foldera projekta pokrenite:

```powershell
dotnet run --project PaperlessDesktop
```

U aplikaciji unesite osnovnu adresu Paperless servera i važeći API token, pa izaberite **Učitaj dokumente**. Za fakultetsku instancu koristite pristupne podatke iz uputa za projekat. Token se unosi u maskirano polje i ne čuva se poslije zatvaranja aplikacije.

Adresu servera možete unaprijed zadati varijablom okruženja `PAPERLESS_SERVER_URL`.

Dokumente možete pregledati u tabeli ili na karticama. Dugmad **Prethodna** i **Sljedeća** učitavaju odgovarajuću stranicu sa servera. Promjena prikaza zadržava trenutnu stranicu.
