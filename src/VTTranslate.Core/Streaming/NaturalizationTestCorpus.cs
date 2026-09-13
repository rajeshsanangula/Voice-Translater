namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.14. A realistic (not textbook-perfect) German↔English
/// test corpus covering every category listed in the Step 5.14 instruction (A-S), both
/// directions. Used by the live-test harness and referenced in
/// docs/design-notes/translation-naturalization-experiment.md's quality-comparison table.
/// BaselineTranslation values here are hand-authored, human/heuristic reference
/// translations for comparison purposes ONLY — they are not Azure output and are labeled
/// as such wherever they appear in the design notes (never presented as PROVEN Azure
/// behavior). No real user speech — entirely authored for this experiment.
/// </summary>
public static class NaturalizationTestCorpus
{
    public sealed record CorpusCase(
        string Id, string Category, string SourceLanguage, string TargetLanguage,
        string SourceText, string ReferenceBaselineTranslation, IReadOnlyList<string>? TerminologyTerms = null);

    public static readonly IReadOnlyList<CorpusCase> Cases = new List<CorpusCase>
    {
        new("A1", "Normal conversation", "de", "en", "Guten Tag, wie geht es Ihnen heute?", "Good day, how are you today?"),
        new("A2", "Normal conversation", "en", "de", "I'm doing well, thank you for asking.", "Mir geht es gut, danke der Nachfrage."),

        new("B1", "Informal conversation", "de", "en", "Na, alles klar bei dir?", "Well, everything okay with you?"),
        new("B2", "Informal conversation", "en", "de", "Yeah, no worries, it's all good.", "Ja, keine Sorge, alles gut."),

        new("C1", "Business conversation", "de", "en", "Wir müssen den Vertrag bis Freitag unterschreiben.", "We need to sign the contract by Friday."),
        new("C2", "Business conversation", "en", "de", "Please forward the invoice to accounting.", "Bitte leiten Sie die Rechnung an die Buchhaltung weiter."),

        new("D1", "Technical conversation", "de", "en", "Der Server wirft eine Nullzeiger-Ausnahme in der Produktionsumgebung.",
            "The server throws a null pointer exception in the production environment.", new[] { "Nullzeiger-Ausnahme", "null pointer exception" }),
        new("D2", "Technical conversation", "en", "de", "The API returns a 500 error when the payload exceeds the buffer size.",
            "Die API gibt einen 500-Fehler zurück, wenn die Nutzlast die Puffergröße überschreitet.", new[] { "API", "buffer" }),

        new("E1", "Idioms", "de", "en", "Das ist nicht mein Bier.", "That's not my problem."),
        new("E2", "Idioms", "en", "de", "It's raining cats and dogs outside.", "Es gießt in Strömen draußen."),

        new("F1", "Short replies", "de", "en", "Ja.", "Yes."),
        new("F2", "Short replies", "en", "de", "Okay.", "Okay."),

        new("G1", "Long sentences", "de", "en",
            "Nachdem wir alle Optionen besprochen hatten, die uns zu diesem Zeitpunkt zur Verfügung standen, entschieden wir uns schließlich dafür, das Projekt um zwei Wochen zu verschieben, um zusätzliche Ressourcen zu sichern.",
            "After discussing all the options available to us at that time, we finally decided to postpone the project by two weeks to secure additional resources."),
        new("G2", "Long sentences", "en", "de",
            "Even though the weather forecast predicted heavy rain throughout the entire weekend, the team decided to proceed with the outdoor event as originally planned.",
            "Obwohl die Wettervorhersage das ganze Wochenende über starken Regen vorhersagte, entschied sich das Team, die Outdoor-Veranstaltung wie ursprünglich geplant durchzuführen."),

        new("H1", "German subordinate clauses", "de", "en", "Ich glaube, dass er heute nicht kommen wird, weil er krank ist.",
            "I believe that he will not come today because he is sick."),
        new("H2", "German subordinate clauses", "en", "de", "She said that she would call back once she had the report.",
            "Sie sagte, dass sie zurückrufen würde, sobald sie den Bericht hätte."),

        new("I1", "German separable verbs", "de", "en", "Ich rufe dich heute Abend an.", "I will call you this evening."),
        new("I2", "German separable verbs", "de", "en", "Wir laden dich herzlich zur Feier ein.", "We warmly invite you to the celebration."),

        new("J1", "Self-corrections", "de", "en", "Wir treffen uns um drei — nein, Entschuldigung, um vier Uhr.", "We're meeting at three — no, sorry, at four o'clock."),
        new("J2", "Self-corrections", "en", "de", "Send it to John — actually, send it to Sarah instead.", "Schick es an John — eigentlich, schick es stattdessen an Sarah."),

        new("K1", "Incomplete speech", "de", "en", "Ich wollte eigentlich nur sagen, dass...", "I actually just wanted to say that..."),
        new("K2", "Incomplete speech", "en", "de", "So if we could just, I mean, maybe we should...", "Also wenn wir nur, ich meine, vielleicht sollten wir..."),

        new("L1", "Questions", "de", "en", "Können Sie mir bitte den Weg zum Bahnhof zeigen?", "Can you please show me the way to the train station?"),
        new("L2", "Questions", "en", "de", "Do you know what time the meeting starts?", "Wissen Sie, um wie viel Uhr das Meeting beginnt?"),

        new("M1", "Negation", "de", "en", "Ich habe das nicht gesagt.", "I did not say that."),
        new("M2", "Negation", "en", "de", "There is no reason to worry.", "Es gibt keinen Grund zur Sorge."),

        new("N1", "Numbers", "de", "en", "Wir haben 42 neue Kunden gewonnen.", "We gained 42 new customers."),
        new("N2", "Numbers", "en", "de", "The total came to 1,250 euros.", "Die Summe betrug 1.250 Euro."),

        new("O1", "Dates", "de", "en", "Das Treffen ist am 14. März geplant.", "The meeting is planned for March 14th."),
        new("O2", "Dates", "en", "de", "The deadline is October 3rd.", "Die Frist ist der 3. Oktober."),

        new("P1", "Names", "de", "en", "Herr Müller hat die Präsentation für Frau Schmidt vorbereitet.", "Mr. Müller prepared the presentation for Ms. Schmidt."),
        new("P2", "Names", "en", "de", "Sarah introduced Michael to the new team lead.", "Sarah stellte Michael dem neuen Teamleiter vor."),

        new("Q1", "Technical terminology", "de", "en", "Wir müssen die Latenz der API reduzieren.", "We need to reduce the latency of the API.", new[] { "API", "latency" }),
        new("Q2", "Technical terminology", "en", "de", "The database connection pool is exhausted.", "Der Datenbank-Verbindungspool ist erschöpft.", new[] { "database", "connection pool" }),

        new("R1", "Ambiguous wording", "de", "en", "Das kann warten.", "That can wait."),
        new("R2", "Ambiguous wording", "en", "de", "I'll see what I can do.", "Ich werde sehen, was ich tun kann."),

        new("S1", "Fast conversational phrasing", "de", "en", "Ja klar mach ich, kein Ding.", "Yeah sure I'll do it, no problem."),
        new("S2", "Fast conversational phrasing", "en", "de", "Gotta run, catch you later.", "Muss los, bis später."),
    };
}
