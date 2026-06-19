# Guía de Oloráculo

Manual de trabajo del proyecto: qué es, cómo funciona por dentro y cómo ponerlo a funcionar paso a paso.

> El README en inglés es la cara pública del repo. Esta guía es el manual interno y se mantiene en español.

---

## 1. ¿Qué es y para qué sirve?

Oloráculo es una aplicación **.NET 9 + Blazor Server** que predice los partidos del Mundial 2026.

No es una sola fórmula mágica: construye la predicción como una **escalera de modelos** (del más simple al más sofisticado), elige automáticamente el mejor modelo que tenga datos suficientes, y **explica** qué modelo usó y qué información le faltó. Además:

- Corre una **simulación Monte Carlo** del torneo completo (quién pasa de grupo, quién llega a la final, quién sale campeón).
- Guarda cada predicción como **snapshot** y luego la **evalúa** contra el resultado real con métricas estándar (Brier, RPS, log loss).
- Enriquece las predicciones con **contexto en vivo**: clima del estadio, moral del equipo, lesiones y bajas.

El objetivo no es solo "adivinar", sino tener un sistema **medible y explicable**: poder responder con datos si las predicciones son buenas y por qué.

---

## 2. Cómo funciona por dentro

### 2.1 El flujo completo

```
CSV semilla ─┐
APIs en vivo ─┼─→ SQLite (EF Core) ─→ Escalera de modelos ─→ Selector final ─→ Predicción
             │                                                      │
             └──────────────────────────────────────────────→ Simulación Monte Carlo
                                                                    │
                                          Snapshots ─→ Evaluación (Brier/RPS/log loss)
```

### 2.2 La escalera de modelos

Cada modelo produce una predicción con una **prioridad** y una bandera de **degradado** (si le faltan datos para ser confiable). De menor a mayor sofisticación:

| Modelo | Qué usa |
| --- | --- |
| **Null (base)** | Reparto uniforme. Siempre disponible, es el piso. |
| **Ranking FIFA** | Diferencia de puntos FIFA entre los equipos. |
| **Elo** | Diferencia de rating Elo. |
| **Forma reciente** | Resultados recientes de cada equipo. |
| **Goles (Poisson)** | Modelo Poisson con ajuste Dixon-Coles para marcadores bajos. Estima fuerza de ataque y vulnerabilidad defensiva ajustadas por rival, con decaimiento temporal (los partidos viejos pesan menos). |
| **Goles + contexto reciente** | El modelo de goles, ajustado por disponibilidad de jugadores, **clima** y **moral**. |

### 2.3 El selector final (`FinalPredictionSelector`)

1. Ordena los modelos por prioridad.
2. Elige el **escalón más alto que NO esté degradado** (el más sofisticado que tenga datos suficientes).
3. Si Elo y FIFA coinciden en un favorito distinto al del modelo elegido, aplica un **sesgo de consenso del 15%** hacia ese favorito (calibración).
4. Devuelve la predicción con una explicación de qué modelo se usó, qué se omitió y por qué.

> Detalle importante: "Goles + contexto reciente" solo es **no degradado** cuando efectivamente se aplicó algún contexto (clima, moral o disponibilidad). Si el clima y la moral fallan o están vacíos, el sistema cae silenciosamente al modelo de goles pelado.

### 2.4 Fuentes de datos

**CSV semilla** (en `Oloraculo.Web/Data/`):
- `wc2026_groups.csv` — grupos y equipos.
- `historical_results.csv` — resultados históricos (alimenta el modelo de goles).
- `fifa_rankings.csv` — ranking FIFA.
- `elo_snapshot.csv` — ratings Elo.
- `goalscorers.csv` — goleadores (cache opcional).

**En vivo (opcional):**
- **Ranking FIFA** desde Wikipedia (se cachea 7 días para no golpear el rate limit).
- **Elo** desde international-football.net.
- **API-Football** (requiere clave) — fixtures, lesiones, alineaciones, cuotas.
- **Noticias de disponibilidad** vía OpenRouter (clasifica artículos de lesiones).
- **Clima** vía OpenRouter / Perplexity (busca estadio y pronóstico).
- **Moral** vía OpenRouter / Perplexity (analiza 4 señales del equipo).

### 2.5 Clima y moral (lo nuevo)

**Clima** (`WeatherService`): una llamada a Perplexity (`perplexity/sonar`, con búsqueda web) que devuelve ciudad, estadio, temperatura, humedad, probabilidad de lluvia y condición del partido. Con la temperatura se calcula una **ventaja climática** por equipo según una tabla de "zona de confort" (un equipo tropical jugando en frío pierde ventaja, y viceversa).

**Moral** (`TeamMoraleService`): una llamada a Perplexity por equipo que analiza **exactamente 4 señales**:
1. Lesiones y estado físico.
2. Problemas personales/familiares de jugadores.
3. Contexto del partido anterior (resultado y rendimiento).
4. Moral y dinámica del equipo (declaraciones, conflictos, presión).

Devuelve un ajuste entre `-0.08` y `+0.08` que multiplica los goles esperados del equipo.

### 2.6 Simulación del torneo

`SimulationService` corre N simulaciones Monte Carlo (por defecto 10.000, semilla 2026 para reproducibilidad). Para cada simulación juega los grupos, calcula los terceros mejores, arma el cuadro de eliminación del Mundial 2026 y cuenta cuántas veces cada equipo pasa de fase / llega a la final / sale campeón.

### 2.7 Evaluación

Cada predicción guardada se compara con el resultado real usando:
- **Brier score** — error cuadrático de probabilidades (más bajo = mejor).
- **RPS** (Ranked Probability Score) — penaliza más los errores "lejos" del resultado.
- **Log loss** — penaliza fuerte la confianza mal puesta.
- **Precisión del favorito** — % de veces que el favorito acertó.

---

## 3. Cómo hacerlo funcionar (paso a paso)

### 3.1 Requisitos

- **.NET 9 SDK** instalado.
- (Opcional pero recomendado) una **clave de OpenRouter** para clima, moral y disponibilidad.

### 3.2 Primera corrida

```bash
dotnet restore
dotnet run --project Oloraculo.Web
```

En el primer arranque:
- Se crea la base SQLite automáticamente (`oloraculo.db`).
- Se importan los CSV semilla.
- Se intenta refrescar rankings (FIFA/Elo) — si Wikipedia responde 429, no pasa nada, usa el CSV cacheado.

Abrí el navegador en la URL que muestra la consola (por defecto `http://localhost:5000` o el puerto que asigne).

### 3.3 Configurar la clave de OpenRouter

La clave **no va en `appsettings.json`** (ese archivo se commitea). Va en `appsettings.Development.json`, que está en `.gitignore`:

```json
{
  "Oloraculo": {
    "OpenRouterApiKey": "sk-or-v1-tu-clave-aca"
  }
}
```

Sin esta clave, las predicciones siguen funcionando (modelos base + goles), pero los botones de clima y moral quedan deshabilitados.

### 3.4 Flujo de uso recomendado: partido por partido

Este es el flujo correcto (más preciso y más barato que analizar todo de golpe):

1. **Andá a `/matches`** y seleccioná el partido que querés predecir.
2. **Analizar clima** — busca el estadio específico y trae el pronóstico.
3. **Analizar moral** — analiza solo los 2 equipos de ese partido.
4. **Predecir ahora** — genera la predicción con el contexto ya cargado.
5. **Guardar predicción** — crea el snapshot con fecha y hora (necesario para que luego se evalúe).

> El snackbar de la predicción NO se guarda solo. Si no apretás "Guardar predicción", no queda registro para evaluar después.

### 3.5 Cargar resultados y medir

1. Cuando un partido se juega, cargá el resultado real en `/matches`.
2. Andá a `/performance` y apretá **Recalcular** — escanea los partidos jugados, busca el snapshot previo de cada uno y genera la evaluación.
3. Mirá la tabla completa ordenable por RPS/Brier para ver dónde el oráculo acertó y dónde falló.

---

## 4. Pantallas

| Ruta | Para qué |
| --- | --- |
| `/` | Resumen y escalera de modelos. |
| `/lab` | Comparar dos equipos cualquiera a través de toda la escalera. |
| `/matches` | Fixtures de grupos, predicción, contexto (clima/moral), carga de resultados. |
| `/fixture` | Vista completa de un partido. |
| `/tournament` | Correr la simulación Monte Carlo. |
| `/tournament/snapshots` | Revisar proyecciones guardadas. |
| `/performance` | Métricas de evaluación + Recalcular. |
| `/data` | Importación CSV, refresh de rankings, API-Football, disponibilidad. |

---

## 5. Configuración (claves útiles)

Todo vive en `appsettings.json` bajo la sección `Oloraculo`. Las más importantes:

| Clave | Qué hace |
| --- | --- |
| `SimulationCount` / `SimulationSeed` | Cantidad de simulaciones y semilla reproducible. |
| `RecentResultCount` | Cuántos partidos recientes usa la forma reciente. |
| `GoalModelYearsWindow` | Ventana de años del modelo de goles. |
| `RankingRefreshOnStartup` | Si refresca rankings al arrancar. |
| `OpenRouterApiKey` | Clave para clima/moral/disponibilidad (en Development). |
| `OpenRouterModel` | Modelo barato para tareas simples (`openai/gpt-4o-mini`). |
| `MoraleModel` | Modelo con búsqueda web para clima y moral (`perplexity/sonar`). |
| `ApiFootballApiKey` | Clave opcional de API-Football. |

---

## 6. Problemas frecuentes (troubleshooting)

| Síntoma | Causa y solución |
| --- | --- |
| `429 Too many requests` al refrescar rankings FIFA | Wikipedia limita las peticiones. El sistema cachea el ranking 7 días, así que es inofensivo. Se resuelve solo. |
| Clima o moral dan "0" o no funcionan | Falta `OpenRouterApiKey` en `appsettings.Development.json`, o la respuesta vino envuelta en markdown (ya se maneja con `ExtractJson`). |
| "No se pudo generar la predicción" | El modelo de contexto quedó degradado. Cargá clima o moral primero para que el escalón superior sea usable. |
| Error de build "archivo en uso" | La app está corriendo y bloquea el `.exe`. Pará el proceso antes de compilar/testear. |
| Aparece "0" como estado del partido | Es el campo `Status` del CSV con valor numérico; ya se filtra en la UI. |

---

## 7. Tests

```bash
dotnet test
```

Actualmente 125 tests. **Ojo:** `WeatherService` y `TeamMoraleService` todavía no tienen cobertura (ver auditoría).

---

## 8. Pendientes conocidos (deuda técnica)

Resumen de la auditoría — ver detalle completo en la conversación o en una eventual `DOCS/AUDITORIA.md`:

1. **Migraciones EF** — hoy se usa `EnsureCreated` + SQL a mano. Y el drift está esparcido: hay `CREATE/ALTER TABLE` en **tres** archivos (`Program.cs`, `CsvImportService.cs`, `SnapshotService.cs`). Conviene migrar a EF Migrations y centralizar el esquema.
2. **Tests de clima y moral** — los servicios más frágiles no tienen cobertura.
3. **Guard de frescura** — `MoraleStaleAfterHours` está definido pero no se usa; cada clic re-gasta en la API.
4. **Clima por LLM** — los números del pronóstico los inventa Perplexity; un híbrido con Open-Meteo sería más preciso.
5. **Evaluar todos los escalones** — hoy solo se evalúa "Oráculo final", así que no se puede comprobar si clima/moral realmente mejoran las predicciones.
6. **Servicios y componentes demasiado grandes (god-services)** — varios archivos mezclan fetching, parsing, clasificación, persistencia y presentación. Conteos reales verificados:

   | Archivo | Líneas |
   | --- | ---: |
   | `AvailabilityNewsService.cs` | 775 |
   | `SnapshotService.cs` | 753 |
   | `Matches.razor` | 622 |
   | `ReadmeSnapshotExportService.cs` | 590 |
   | `ApiFootballService.cs` | 508 |

   Conviene separar responsabilidades (fetching / parsing / clasificación / persistencia / presentación) y extraer componentes o presenters de `Matches.razor`, que además inyecta demasiados servicios.
