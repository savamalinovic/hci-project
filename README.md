# Paperless Desktop

Windows klijent za Paperless-ngx, napravljen u C# i WPF-u. [HCI analiza](HCI_Analiza.pdf) sadrži rezultate poređenja web i desktop interfejsa.

## Pokretanje

Potreban je Windows i .NET 8 SDK. Iz osnovnog foldera projekta pokrenite:

```powershell
dotnet run --project PaperlessDesktop
```

Unesite adresu Paperless servera i API token, pa izaberite **Učitaj dokumente**. Adresu možete zadati i varijablom `PAPERLESS_SERVER_URL`. Aplikacija ne sačuvava token nakon zatvaranja.

## Rad s dokumentima

- Dokumente možete prikazati u tabeli ili na karticama i filtrirati po ključnim riječima, tagu, korespondentu i tipu.
- Izbor dokumenta otvara pregled s listanjem PDF stranica, zumom od 50% do 200% i prepoznatim tekstom, ako je dostupan.
- U prozoru **Dodaj dokument** izaberite fajl, po želji unesite metapodatke i pošaljite ga. Poruka **Dokument je dodat** pojavljuje se kad dokument postane dostupan. Dugme **Provjeri status** ponavlja provjeru bez ponovnog slanja.
- U prozoru **Upravljaj tagovima** možete dodavati, uređivati i brisati tagove. U **Podešavanjima** možete promijeniti temu, akcentnu boju i jezik.

## Pretraga prirodnim jezikom

Izaberite **Gemini** ili **Lokalni parser**, unesite upit poput „Nađi račune iz juna 2025.“ i kliknite **Protumači**. Po potrebi uredite predložene filtere pa kliknite **Pretraži**. Datumske granice su uključive.

Za Gemini postavite `GEMINI_API_KEY` prije pokretanja aplikacije. Aplikacija ne sačuvava ključ u lokalnim postavkama. Model možete promijeniti varijablom `GEMINI_MODEL`. Lokalni parser radi bez ključa i podržava jednostavne upite na srpskom i engleskom.

Za tematske upite, poput „Daj knjige o biljkama“, možete odobriti slanje naziva i ID-jeva do 250 dokumenata da Gemini predloži konkretne rezultate. Fajlovi, OCR tekst i Paperless token ostaju u komunikaciji s Paperless serverom.
