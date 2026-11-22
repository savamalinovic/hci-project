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

Unesite ključne riječi ili odaberite tag, korespondenta i tip dokumenta, pa izaberite **Pretraži**. Izaberite dokument u rezultatima za prikaz metapodataka i pregleda. PDF pregled se lista strelicama, a tekst dokumenta je dostupan kada ga je Paperless prepoznao.

U prozoru **Dodaj dokument** izaberite fajl, po želji unesite metapodatke i pokrenite slanje. Prikaz razlikuje slanje od obrade na serveru. Ako zatvorite prozor nakon prijema, možete ga ponovo otvoriti da nastavite provjeru statusa u istoj sesiji.

Prozor **Upravljaj tagovima** omogućava kreiranje, uređivanje i brisanje tagova. Nakon promjene lista dokumenata i filteri učitavaju ažurirane nazive.

U prozoru **Podešavanja** odaberite svijetlu ili tamnu temu, akcentnu boju i srpski ili engleski jezik. Akcent se bira RGB klizačima ili unosom vrijednosti u formatu `#RRGGBB`. Promjene se primjenjuju nakon čuvanja. Aplikacija ih ponovo učitava iz `%LOCALAPPDATA%\PaperlessDesktop\settings.json` pri pokretanju.

## Pretraga prirodnim jezikom

Nakon učitavanja dokumenata otvorite **Pretraga prirodnim jezikom**. Unesite, na primjer, „Nađi račune iz juna 2025.“ i izaberite **Protumači**. Pregledajte i po potrebi uredite predložene filtere, pa izaberite **Pretraži**. Datumske granice su uključive, a **Očisti** uklanja i datum i ostale filtere.

Za tumačenje putem Geminija postavite `GEMINI_API_KEY` u okruženju iz kojeg pokrećete aplikaciju. `GEMINI_MODEL` je opciona zamjena za podrazumijevani `gemini-3.5-flash-lite`. Ključ se ne sprema u lokalne postavke. Gemini dobija upit, trenutni datum i raspoložive nazive i ID-jeve filtera. Paperless token i sadržaj dokumenata koriste se samo u komunikaciji s Paperlessom.

Ako Gemini nije podešen ili ne odgovori, aplikacija označi prijedlog lokalnog parsera. Lokalni parser pokriva jednostavne upite na srpskom i engleskom, a filtere možeš dopuniti ručno prije pretrage.
