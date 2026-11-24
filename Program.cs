using Microsoft.Data.Sqlite;                                            // usa la libreria con funzioni SQL

using var database = new SqliteConnection(                              // prepara la connessione; ordini.db è il file usato, foreign keys=true abilita i controlli sui riferimenti
    "Data Source=ordini.db;Foreign Keys=True");                         // tra tabelle; using var fa sì che la con. venga rilasciata quando termina il programma.

database.Open();
InizializzaDatabase(database);

while (true)                                                            // serve per avere il menù sempre attivo e ripeterlo
{
    Console.WriteLine("\n--- Gestione ordini ---");
    Console.WriteLine("1. Elenca prodotti");
    Console.WriteLine("2. Crea ordine");
    Console.WriteLine("3. Elenca ordini aperti");
    Console.WriteLine("4. Rifornisci prodotto");
    Console.WriteLine("0. Esci");
    Console.Write("Scelta: ");

    switch (Console.ReadLine())                                         // permette la navigazione
    {
        case "1":
            ElencaProdotti(database);
            break;

        case "2":
            CreaOrdine(database);
            break;

        case "3":
            ElencaOrdiniAperti(database);
            break;

        case "4":
            RifornisciProdotto(database);
            break;
        
        case "0":
        case null:
            return;

        default:
            Console.WriteLine("Scelta non valida.");
            break;
    }
}

static void InizializzaDatabase(SqliteConnection database)              // prepara le tabelle
{
    using var comando = database.CreateCommand();                       // crea un oggeto "comando" legato alla connessione
                                                                        // CommandText associa il testo SQL all'oggetto
    comando.CommandText = """
        CREATE TABLE IF NOT EXISTS prodotti (
            id INTEGER PRIMARY KEY,
            nome TEXT NOT NULL,
            giacenza INTEGER NOT NULL CHECK (giacenza >= 0)
        );

        CREATE TABLE IF NOT EXISTS ordini (
            id INTEGER PRIMARY KEY,
            stato TEXT NOT NULL CHECK (stato IN ('APERTO', 'EVASO'))
        );

        CREATE TABLE IF NOT EXISTS righe_ordini (
            ordine_id INTEGER NOT NULL REFERENCES ordini(id),
            prodotto_id INTEGER NOT NULL REFERENCES prodotti(id),
            quantita INTEGER NOT NULL CHECK (quantita > 0),
            PRIMARY KEY (ordine_id, prodotto_id)
        );

        INSERT OR IGNORE INTO prodotti (id, nome, giacenza) VALUES
            (1, 'Divano', 5),
            (2, 'Poltrona', 1),
            (3, 'Pouf', 0);
        """;

    comando.ExecuteNonQuery();                                          // esegue il comando creato
}

static void ElencaProdotti(SqliteConnection database)                   // legge le giacenze
{
    using var comando = database.CreateCommand();

    comando.CommandText = """
        SELECT id, nome, giacenza
        FROM prodotti
        ORDER BY id;
    """;

    using var lettore = comando.ExecuteReader();                        // apre un lettore sui risultati di "SELECT"
    Console.WriteLine("\nProdotti");                                    // ogni lettore restituisce "true" finché
                                                                        // ci sono righe da leggere
    while (lettore.Read())
    {
        long id = lettore.GetInt64(0);          // id
        string nome = lettore.GetString(1);     // nome
        long giacenza = lettore.GetInt64(2);    // giacenza

        Console.WriteLine($"{id} | {nome} | Giacenza: {giacenza}");     // inserisce i valori e stampa una riga per prodotto
    }
}

static void CreaOrdine(SqliteConnection database)                       // valida l'input e salva le modifiche
{
    Console.Write("ID prodotto: ");                                     // TryParse converte il testo della console in un numero
    if (!long.TryParse(Console.ReadLine(), out long idProdotto) || idProdotto <= 0)
    {
        Console.WriteLine("Inserisci un ID prodotto intero positivo.");
        return;
    }

    Console.Write("Quantità: ");
    if (!int.TryParse(Console.ReadLine(), out int quantita) || quantita <= 0)
    {
        Console.WriteLine("Inserisci una quantità intera maggiore di zero.");
        return;
    }

    try
    {
        using var transazione = database.BeginTransaction();            // comincia la transizione
    
        using var aggiornaGiacenza = database.CreateCommand();
        aggiornaGiacenza.Transaction =  transazione;                    // UPDATE controlla e scala la giacenza
        aggiornaGiacenza.CommandText = """
            UPDATE prodotti
            SET giacenza = giacenza - $quantita
            WHERE id = $id AND giacenza >= $quantita;
            """;
        aggiornaGiacenza.Parameters.AddWithValue("$id", idProdotto);
        aggiornaGiacenza.Parameters.AddWithValue("$quantita", quantita);

        int righeAggiornate = aggiornaGiacenza.ExecuteNonQuery();       // ExecuteNonQuery indica quante righe
                                                                        // sono state modificate
        if (righeAggiornate == 0)
        {
            using var cercaProdotto = database.CreateCommand();
            cercaProdotto.Transaction = transazione;
            cercaProdotto.CommandText = """
                SELECT 1
                FROM prodotti
                WHERE id = $id;
                """;
            cercaProdotto.Parameters.AddWithValue("$id", idProdotto);

            bool prodottoEsistente = cercaProdotto.ExecuteScalar() is not null;
            transazione.Rollback();                                     // ExecuteScalar legge (e restituisce)
                                                                        // la prima colonna della prima riga
            Console.WriteLine (prodottoEsistente
                ? "Giacenza insufficiente."
                : "Prodotto inesistente.");
            return;
        }

        using var inserisciOrdine = database.CreateCommand();
        inserisciOrdine.Transaction = transazione;
        inserisciOrdine.CommandText = """
            INSERT INTO ordini (stato)
            VALUES ('APERTO')
            RETURNING id;
            """;

        long idOrdine = Convert.ToInt64(inserisciOrdine.ExecuteScalar());

        using var inserisciRiga = database.CreateCommand();
        inserisciRiga.Transaction = transazione;
        inserisciRiga.CommandText = """
            INSERT INTO righe_ordini (ordine_id, prodotto_id, quantita)
            VALUES ($ordine, $prodotto, $quantita);
            """;
        inserisciRiga.Parameters.AddWithValue("$ordine", idOrdine);
        inserisciRiga.Parameters.AddWithValue("$prodotto", idProdotto);
        inserisciRiga.Parameters.AddWithValue("$quantita", quantita);
        inserisciRiga.ExecuteNonQuery();

        transazione.Commit();
        Console.WriteLine($"Ordine {idOrdine} creato.");
    }
    catch (SqliteException errore)
    {
        Console.WriteLine($"Ordine non salvato. Errore SQLite: {errore.Message}");
    }
}

static void ElencaOrdiniAperti(SqliteConnection database)               // calcola quanti pezzi ci sono in ogni ordine
{
    using var comando = database.CreateCommand();

    comando.CommandText = """
        SELECT o.id, SUM(r.quantita) AS pezzi
        FROM ordini AS o
        JOIN righe_ordini AS r ON r.ordine_id = o.id
        WHERE o.stato = 'APERTO'
        GROUP BY o.id
        ORDER BY o.id;
        """;

    using var lettore = comando.ExecuteReader();                        // scorre tutte le righe leggendole
                                                                        // una alla volta
    Console.WriteLine("\nOrdini aperti");

    if (!lettore.HasRows)
    {
        Console.WriteLine("Nessun ordine aperto.");
        return;
    }

    while (lettore.Read())
    {
        long idOrdine = lettore.GetInt64(0);
        long pezzi = lettore.GetInt64(1);

        Console.WriteLine($"Ordine {idOrdine} | Pezzi: {pezzi}");
    }
}

static void RifornisciProdotto(SqliteConnection database)
{
    Console.Write("ID prodotto: ");
    if (!long.TryParse(Console.ReadLine(), out long idProdotto) || idProdotto <= 0)
    {
        Console.WriteLine("Inserisci un ID prodotto intero positivo.");
        return;
    }

    Console.Write("Pezzi da aggiungere: ");
    if (!int.TryParse(Console.ReadLine(), out int quantita) || quantita <= 0)
    {
        Console.WriteLine("Inserisci una quantità intera maggiore di zero.");
        return;
    }

    using var comando = database.CreateCommand();
    comando.CommandText = """
        UPDATE prodotti
        SET giacenza = giacenza + $quantita
        WHERE id = $id;
        """;

    comando.Parameters.AddWithValue("$id", idProdotto);
    comando.Parameters.AddWithValue("$quantita", quantita);

    int righeAggiornate = comando.ExecuteNonQuery();

    if (righeAggiornate == 0)
    {
        Console.WriteLine("Prodotto Inesistente. ");
        return;
    }

    Console.WriteLine($"Aggiungi {quantita} pezzi alla giacenza");
}