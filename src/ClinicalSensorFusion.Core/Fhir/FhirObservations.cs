using System.Globalization;
using System.Text.Json.Nodes;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Fhir;

/// <summary>
/// Indicatori di una rilevazione come corpi di risorse <c>Observation</c> HL7 FHIR: codice, valore, metodo e
/// componenti. Mancano di proposito i campi che dipendono da chi le pubblica (identificativo, paziente,
/// operatore, date): li aggiunge il server per le sue API, e non li aggiunge l'app quando manda i dati al
/// modello linguistico, che non deve ricevere nulla di identificativo.
/// </summary>
/// <remarks>
/// Parametri vitali e passi hanno codice LOINC e unità UCUM; escursione articolare, tremore, alzate dalla
/// sedia e gli altri indicatori non hanno un codice standard verificato e usano il sistema di codifica
/// locale <see cref="IndicatorSystem"/>, dichiarato come tale. La struttura è la stessa in FHIR R4 e R5.
/// </remarks>
public static class FhirObservations
{
    public const string Loinc = "http://loinc.org";
    public const string Ucum = "http://unitsofmeasure.org";
    public const string CategorySystem = "http://terminology.hl7.org/CodeSystem/observation-category";
    public const string InterpretationSystem = "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation";

    /// <summary>Indicatori senza codice standard (escursione articolare, tremore, alzate dalla sedia, …).</summary>
    public const string IndicatorSystem = "urn:clinical-sensor-fusion:indicator";

    private const string HeartRateChannel = "Frequenza cardiaca";
    private const string Spo2Channel = "SpO2";
    private const string TemperatureChannel = "Temperatura";

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>
    /// Indicatori clinici pubblicati dalle API FHIR del server: parametri vitali, passi, escursione
    /// articolare, tremore, alzate dalla sedia. La chiave è stabile e fa da suffisso dell'identificativo.
    /// </summary>
    public static IReadOnlyList<(string Key, JsonObject Body)> Clinical(SessionSummary summary)
    {
        var result = new List<(string, JsonObject)>();

        if (Channel(summary, SensorKind.HeartRate, HeartRateChannel) is { } heartRate)
        {
            result.Add(("heart-rate", Vital("heartrate", [("8867-4", "Heart rate")], heartRate, "beats/minute", "/min", "Sensore Bluetooth di frequenza cardiaca")));
        }

        if (Channel(summary, SensorKind.PulseOximeter, Spo2Channel) is { } spo2)
        {
            result.Add(("spo2", Vital(
                "oxygensat",
                [("2708-6", "Oxygen saturation in Arterial blood"), ("59408-5", "Oxygen saturation in Arterial blood by Pulse oximetry")],
                spo2, "%", "%", "Saturimetro Bluetooth")));
        }

        if (Channel(summary, SensorKind.BodyTemperature, TemperatureChannel) is { } temperature)
        {
            result.Add(("body-temperature", Vital("bodytemp", [("8310-5", "Body temperature")], temperature, "Cel", "Cel", "Termometro Bluetooth")));
        }

        if (summary.Gait is { } gait)
        {
            result.Add(("steps", new JsonObject
            {
                ["category"] = Category("activity", "Activity"),
                ["code"] = Code(Loinc, [("55423-8", "Number of steps in unspecified time Pedometer")], "Passi"),
                ["valueQuantity"] = Quantity(gait.Steps, "steps", "{steps}"),
                ["method"] = new JsonObject { ["text"] = "Pedometro del telefono" },
            }));
        }

        foreach (var (joint, range) in summary.JointRanges.OrderBy(j => j.Key))
        {
            var name = SummaryReport.JointName(joint);
            result.Add(("rom-" + Kebab(joint.ToString()), new JsonObject
            {
                ["category"] = Category("exam", "Exam"),
                ["code"] = Local("rom-" + Kebab(joint.ToString()), $"Escursione articolare: {name}"),
                ["valueQuantity"] = Quantity(range.RangeOfMotion, "deg", "deg"),
                ["method"] = new JsonObject { ["text"] = "Stima ottica (MediaPipe, con profondità LiDAR se attiva)" },
                ["note"] = Note($"Stima ottica con incertezza di ±5–10°, su {range.Samples} fotogrammi."),
                ["component"] = new JsonArray(
                    Component("angle-min", "Angolo minimo", range.Min, "deg", "deg"),
                    Component("angle-max", "Angolo massimo", range.Max, "deg", "deg"),
                    Component("angle-mean", "Angolo medio", range.Mean, "deg", "deg")),
            }));
        }

        if (summary.Tremor is { IsSignificant: true } tremor)
        {
            result.Add(("tremor", new JsonObject
            {
                ["category"] = Category("exam", "Exam"),
                ["code"] = Code(IndicatorSystem, [("tremor-frequency", "Frequenza dominante del tremore (3–12 Hz)")], "Frequenza dominante del tremore"),
                ["valueQuantity"] = Quantity(tremor.DominantFrequencyHz, "Hz", "Hz"),
                ["method"] = new JsonObject { ["text"] = "Accelerometro del telefono, banda 3–12 Hz" },
                ["component"] = new JsonArray(Component("tremor-amplitude-rms", "Ampiezza RMS in banda", tremor.BandRmsMs2, "m/s2", "m/s2")),
            }));
        }

        if (summary.SitToStandCount is { } rises)
        {
            result.Add(("sit-to-stand", new JsonObject
            {
                ["category"] = Category("exam", "Exam"),
                ["code"] = Code(IndicatorSystem, [("sit-to-stand-count", "Alzate dalla sedia contate")], "Alzate dalla sedia"),
                ["valueInteger"] = rises,
                ["method"] = new JsonObject { ["text"] = "Conteggio automatico dalla flessione del ginocchio" },
            }));
        }

        return result;
    }

    /// <summary>
    /// Il resto del riepilogo, che serve a chi deve interpretare la rilevazione (il modello linguistico):
    /// esito del test, cammino, tocco delle dita, voce, contesto dei sensori e qualità dell'acquisizione.
    /// La posizione GPS non c'è mai: il luogo identifica il paziente.
    /// </summary>
    public static IReadOnlyList<(string Key, JsonObject Body)> Context(SessionSummary summary)
    {
        var result = new List<(string, JsonObject)>();

        if (summary.Test is { } test)
        {
            var body = new JsonObject
            {
                ["category"] = Category("exam", "Exam"),
                ["code"] = Local("test-" + test.TestId, test.TestName),
                ["note"] = Note(test.Estimated ? $"{test.Interpretation} Valore stimato in automatico, da verificare." : test.Interpretation),
            };
            if (test.Value is { } value && double.IsFinite(value))
            {
                body["valueQuantity"] = test.Kind switch
                {
                    TestResultKind.Seconds => Quantity(value, "s", "s"),
                    TestResultKind.Repetitions => Quantity(value, "alzate", "{count}"),
                    TestResultKind.MetersPerSecond => Quantity(value, "m/s", "m/s"),
                    TestResultKind.Meters => Quantity(value, "m", "m"),
                    _ => Quantity(value, "Hz", "Hz"),
                };
            }
            else
            {
                body["dataAbsentReason"] = new JsonObject { ["text"] = "Punteggio non ricavabile dai dati" };
            }

            if (test.Flag)
            {
                body["interpretation"] = new JsonArray(new JsonObject
                {
                    ["coding"] = new JsonArray(new JsonObject { ["system"] = InterpretationSystem, ["code"] = "A", ["display"] = "Abnormal" }),
                    ["text"] = "Oltre la soglia di attenzione della fonte del test",
                });
            }

            result.Add(("test", body));
        }

        if (summary.Gait is { } gait)
        {
            var components = new JsonArray();
            if (double.IsFinite(gait.DistanceMeters))
            {
                components.Add((JsonNode)Component("gait-distance", "Distanza stimata", gait.DistanceMeters, "m", "m"));
            }

            if (gait.CadenceStepsPerMinute is { } cadence && double.IsFinite(cadence))
            {
                components.Add((JsonNode)Component("gait-cadence", "Cadenza", cadence, "steps/min", "{steps}/min"));
            }

            components.Add((JsonNode)Component("floors-ascended", "Piani saliti", gait.FloorsAscended, "piani", "{floors}"));
            components.Add((JsonNode)Component("floors-descended", "Piani scesi", gait.FloorsDescended, "piani", "{floors}"));
            result.Add(("gait", new JsonObject
            {
                ["category"] = Category("activity", "Activity"),
                ["code"] = Local("gait", "Cammino (pedometro del telefono)"),
                ["component"] = components,
            }));
        }

        if (summary.FingerTaps is { } series && FingerTapMetrics.From(series, 0) is { Taps: > 0 } taps)
        {
            var components = new JsonArray();
            AddOptional(components, "tap-frequency", "Frequenza dei tocchi", taps.FrequencyHz, "Hz", "Hz");
            AddOptional(components, "tap-amplitude", "Ampiezza media (frazione della lunghezza del palmo)", taps.MeanAmplitude, "1", "1");
            AddOptional(components, "tap-amplitude-decrement", "Calo di ampiezza tra i primi e gli ultimi tocchi", taps.AmplitudeDecrement, "1", "1");
            AddOptional(components, "tap-interval-variability", "Irregolarità del ritmo (coefficiente di variazione)", taps.IntervalVariability, "1", "1");
            var body = new JsonObject
            {
                ["category"] = Category("exam", "Exam"),
                ["code"] = Local("finger-taps", "Tocchi indice-pollice rilevati"),
                ["valueInteger"] = taps.Taps,
                ["method"] = new JsonObject { ["text"] = "Punti della mano di MediaPipe Hands" },
            };
            if (components.Count > 0)
            {
                body["component"] = components;
            }

            result.Add(("finger-taps", body));
        }

        if (summary.Tremor is { IsSignificant: false })
        {
            result.Add(("tremor", new JsonObject
            {
                ["category"] = Category("exam", "Exam"),
                ["code"] = Local("tremor-frequency", "Frequenza dominante del tremore (3–12 Hz)"),
                ["dataAbsentReason"] = new JsonObject { ["text"] = "Nessun tremore significativo nella banda 3–12 Hz" },
                ["method"] = new JsonObject { ["text"] = "Accelerometro del telefono, banda 3–12 Hz" },
            }));
        }

        if (summary.Audio is { } audio)
        {
            result.Add(("audio", new JsonObject
            {
                ["category"] = Category("exam", "Exam"),
                ["code"] = Local("audio-level", "Livello medio dell'audio"),
                ["valueQuantity"] = PlainQuantity(audio.RmsDbfs, "dBFS"),
                ["method"] = new JsonObject { ["text"] = "Microfono del telefono, 48 kHz" },
                ["component"] = new JsonArray(
                    PlainComponent("audio-peak", "Picco", audio.PeakDbfs, "dBFS"),
                    Component("audio-active-ratio", "Tempo con segnale sopra −45 dBFS", audio.ActiveRatio * 100, "%", "%"),
                    Component("audio-clipping-ratio", "Campioni saturati", audio.ClippingRatio * 100, "%", "%"),
                    PlainComponent("audio-zero-crossing-rate", "Passaggi per lo zero", audio.ZeroCrossingRate, "1/campione"),
                    Component("audio-duration", "Durata della registrazione", audio.DurationSeconds, "s", "s")),
            }));
        }

        foreach (var (kind, stream) in (summary.Sensors ?? []).Where(s => s.Key != SensorKind.Location).OrderBy(s => s.Key))
        {
            foreach (var (channel, index) in stream.Channels.Select((c, i) => (c, i)))
            {
                // I parametri vitali hanno già la loro osservazione con codice LOINC.
                if ((kind, channel.Name) is (SensorKind.HeartRate, HeartRateChannel) or (SensorKind.PulseOximeter, Spo2Channel) or (SensorKind.BodyTemperature, TemperatureChannel))
                {
                    continue;
                }

                var key = $"sensor-{Kebab(kind.ToString())}-{index}";
                result.Add((key, new JsonObject
                {
                    ["category"] = Category("survey", "Survey"),
                    ["code"] = Local(key, $"{SummaryReport.StreamName(kind)}: {channel.Name}"),
                    ["valueQuantity"] = PlainQuantity(channel.Mean, channel.Unit),
                    ["note"] = Note($"Media di {channel.Samples} campioni durante la rilevazione."),
                    ["component"] = new JsonArray(
                        PlainComponent("value-min", "Minimo", channel.Min, channel.Unit),
                        PlainComponent("value-max", "Massimo", channel.Max, channel.Unit),
                        PlainComponent("value-first", "Primo valore", channel.First, channel.Unit),
                        PlainComponent("value-last", "Ultimo valore", channel.Last, channel.Unit)),
                }));
            }
        }

        result.Add(("quality", Quality(summary)));
        return result;
    }

    /// <summary>Affidabilità tecnica dell'acquisizione: punteggio e fattori controllati.</summary>
    private static JsonObject Quality(SessionSummary summary)
    {
        var reliability = MeasurementReliability.Assess(summary, Italian);
        var components = new JsonArray(Component("session-duration", "Durata della rilevazione", summary.DurationSeconds, "s", "s"));
        AddOptional(components, "subject-distance", "Distanza mediana del soggetto", summary.MedianSubjectDistanceMeters, "m", "m");
        if (summary.LidarCoverage is { } coverage)
        {
            components.Add((JsonNode)Component("lidar-coverage", "Articolazioni con profondità LiDAR valida", coverage * 100, "%", "%"));
        }

        components.Add((JsonNode)Component("dropped-samples", "Campioni scartati dal registratore", summary.DroppedSamples, "campioni", "{samples}"));
        return new JsonObject
        {
            ["category"] = Category("survey", "Survey"),
            ["code"] = Local("measurement-reliability", "Affidabilità tecnica della misura (0–100)"),
            ["valueInteger"] = reliability.Score,
            ["note"] = new JsonArray([.. reliability.Factors.Select(f => (JsonNode)new JsonObject
            {
                ["text"] = f.Warning ? $"{f.Label}: {f.Value} (fuori soglia)" : $"{f.Label}: {f.Value}",
            })]),
            ["component"] = components,
        };
    }

    /// <summary><c>RightKneeFlexion</c> → <c>right-knee-flexion</c>.</summary>
    public static string Kebab(string pascal) =>
        string.Concat(pascal.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    public static JsonObject Code(string system, (string Code, string Display)[] codes, string text) => new()
    {
        ["coding"] = new JsonArray([.. codes.Select(c => (JsonNode)new JsonObject { ["system"] = system, ["code"] = c.Code, ["display"] = c.Display })]),
        ["text"] = text,
    };

    private static JsonObject Local(string code, string display) => Code(IndicatorSystem, [(code, display)], display);

    private static ChannelStats? Channel(SessionSummary summary, SensorKind kind, string name) =>
        summary.Sensors is { } sensors && sensors.TryGetValue(kind, out var stream) ? stream.Channels.FirstOrDefault(c => c.Name == name) : null;

    /// <summary>Segno vitale secondo il profilo FHIR: valore = media sulla rilevazione, minimo e massimo nella nota.</summary>
    private static JsonObject Vital(string profile, (string Code, string Display)[] codes, ChannelStats stats, string unit, string ucum, string method) => new()
    {
        ["meta"] = new JsonObject { ["profile"] = new JsonArray($"http://hl7.org/fhir/StructureDefinition/{profile}") },
        ["category"] = Category("vital-signs", "Vital Signs"),
        ["code"] = Code(Loinc, codes, codes[0].Display),
        ["valueQuantity"] = Quantity(stats.Mean, unit, ucum),
        ["method"] = new JsonObject { ["text"] = method },
        ["note"] = Note(string.Format(Italian, "Media di {0} misure durante la rilevazione (minimo {1:0.#}, massimo {2:0.#}).", stats.Samples, stats.Min, stats.Max)),
    };

    private static JsonArray Category(string code, string display) => new(new JsonObject
    {
        ["coding"] = new JsonArray(new JsonObject { ["system"] = CategorySystem, ["code"] = code, ["display"] = display }),
    });

    private static JsonArray Note(string text) => new(new JsonObject { ["text"] = text });

    private static JsonObject Quantity(double value, string unit, string ucum)
    {
        var quantity = PlainQuantity(value, unit);
        quantity["system"] = Ucum;
        quantity["code"] = ucum;
        return quantity;
    }

    /// <summary>Grandezza senza unità UCUM (dBFS, unità proprie di un sensore): solo l'etichetta, come FHIR consente.</summary>
    private static JsonObject PlainQuantity(double value, string unit)
    {
        // JSON non ha NaN né infinito: un valore non misurato resta senza "value".
        var quantity = new JsonObject();
        if (double.IsFinite(value))
        {
            quantity["value"] = Math.Round(value, 3);
        }

        if (!string.IsNullOrWhiteSpace(unit))
        {
            quantity["unit"] = unit;
        }

        return quantity;
    }

    private static JsonObject Component(string code, string display, double value, string unit, string ucum) => new()
    {
        ["code"] = Local(code, display),
        ["valueQuantity"] = Quantity(value, unit, ucum),
    };

    private static JsonObject PlainComponent(string code, string display, double value, string unit) => new()
    {
        ["code"] = Local(code, display),
        ["valueQuantity"] = PlainQuantity(value, unit),
    };

    private static void AddOptional(JsonArray components, string code, string display, double? value, string unit, string ucum)
    {
        if (value is { } v && double.IsFinite(v))
        {
            components.Add((JsonNode)Component(code, display, v, unit, ucum));
        }
    }
}
