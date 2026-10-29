using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BibliotecaProbador.Services;

public record Resultado(string Nombre, bool Paso, string Detalle, bool Puntua = true);

public class ProbadorService
{
    private record Paso(string Nombre, Func<Task<(bool Ok, string Detalle)>> Accion, bool Puntua = true);

    public static readonly string[] Controladores = ["Estudiantes", "Libros", "Prestamos", "Devoluciones"];

    public async Task ProbarAsync(string controlador, string urlBase,
        Func<Resultado, int, int, Task> alTerminarPaso)
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri(urlBase.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };

        var pasos = controlador switch
        {
            "Estudiantes" => PasosEstudiantes(http),
            "Libros" => PasosLibros(http),
            "Prestamos" => PasosPrestamos(http),
            "Devoluciones" => PasosDevoluciones(http),
            _ => throw new ArgumentException("Controlador no válido.")
        };

        var hechos = 0;
        foreach (var p in pasos)
        {
            Resultado r;
            try
            {
                var (ok, detalle) = await p.Accion();
                r = new Resultado(p.Nombre, ok, detalle, p.Puntua);
            }
            catch (Exception ex)
            {
                r = new Resultado(p.Nombre, false, ex.Message, p.Puntua);
            }
            await alTerminarPaso(r, ++hechos, pasos.Count);
        }
    }

    // ---------- HELPERS ----------
    private static string U() => Guid.NewGuid().ToString("N")[..8];

    private static string Estado(HttpResponseMessage r, string esperado) =>
        $"Esperado {esperado}, recibido {(int)r.StatusCode}";

    private static async Task<JsonElement?> Leer(HttpResponseMessage r)
    {
        try { return await r.Content.ReadFromJsonAsync<JsonElement>(); }
        catch { return null; }
    }

    private static long Id(JsonElement? j, string campo) =>
        j is { } e && e.TryGetProperty(campo, out var p) && p.TryGetInt64(out var v) ? v : 0;

    private static string? Texto(JsonElement? j, string campo) =>
        j is { } e && e.TryGetProperty(campo, out var p) ? p.GetString() : null;

    private static async Task<(bool, string)> Codigo(Task<HttpResponseMessage> peticion, params int[] esperados)
    {
        var r = await peticion;
        return (esperados.Contains((int)r.StatusCode), Estado(r, string.Join(" o ", esperados)));
    }

    private static object CuerpoEstudiante(string matricula, string nombres = "Prueba Probador") => new
    {
        idEstudiante = 0,
        nombres,
        matricula,
        direccion = "Calle 1",
        correo = "prueba@mail.com",
        telefono = "809-000-0000"
    };

    private static object CuerpoLibro(string isbn, long cantidad, string titulo = "Libro de prueba") => new
    {
        idLibro = 0,
        titulo,
        autor = "Autor de prueba",
        isbn,
        categoria = "Prueba",
        cantidadDisponible = cantidad
    };

    private static async Task<long> NuevoEstudiante(HttpClient h)
    {
        var r = await h.PostAsJsonAsync("api/estudiantes", CuerpoEstudiante("T-" + U()));
        return Id(await Leer(r), "idEstudiante");
    }

    private static async Task<long> NuevoLibro(HttpClient h, long cantidad)
    {
        var r = await h.PostAsJsonAsync("api/libros", CuerpoLibro("ISBN-" + U(), cantidad));
        return Id(await Leer(r), "idLibro");
    }

    private static async Task<long> NuevoPrestamo(HttpClient h, long idE, long idL)
    {
        var r = await h.PostAsJsonAsync("api/prestamos", new { idPrestamo = 0, idEstudiante = idE, idLibro = idL });
        return Id(await Leer(r), "idPrestamo");
    }

    private static async Task<long> Cantidad(HttpClient h, long idL) =>
        Id(await h.GetFromJsonAsync<JsonElement>($"api/libros/{idL}"), "cantidadDisponible");

    // ---------- ESTUDIANTES ----------
    private static List<Paso> PasosEstudiantes(HttpClient h)
    {
        long id = 0;
        var matricula = "T-" + U();

        return
        [
            new("GET /api/estudiantes → 200 con una lista", async () =>
            {
                var r = await h.GetAsync("api/estudiantes");
                var j = await Leer(r);
                return (r.StatusCode == HttpStatusCode.OK && j?.ValueKind == JsonValueKind.Array,
                        Estado(r, "200 y una lista"));
            }),
            new("POST /api/estudiantes → 201 y devuelve el id", async () =>
            {
                var r = await h.PostAsJsonAsync("api/estudiantes", CuerpoEstudiante(matricula));
                id = Id(await Leer(r), "idEstudiante");
                return (r.StatusCode == HttpStatusCode.Created && id > 0, Estado(r, "201") + $", id = {id}");
            }),
            new("GET /api/estudiantes/{id} → 200 con los datos", async () =>
            {
                var r = await h.GetAsync($"api/estudiantes/{id}");
                var ok = r.StatusCode == HttpStatusCode.OK && Texto(await Leer(r), "matricula") == matricula;
                return (ok, Estado(r, "200 con la misma matrícula"));
            }),
            new("GET con id inexistente → 404", () => Codigo(h.GetAsync("api/estudiantes/999999"), 404)),
            new("POST con matrícula repetida → 409",
                () => Codigo(h.PostAsJsonAsync("api/estudiantes", CuerpoEstudiante(matricula)), 409)),
            new("PUT /api/estudiantes/{id} → 204 y guarda el cambio", async () =>
            {
                var r = await h.PutAsJsonAsync($"api/estudiantes/{id}",
                    new
                    {
                        idEstudiante = id, nombres = "Nombre Editado", matricula,
                        direccion = "Calle 1", correo = "prueba@mail.com", telefono = "809-000-0000"
                    });
                var nombre = Texto(await Leer(await h.GetAsync($"api/estudiantes/{id}")), "nombres");
                return (r.StatusCode == HttpStatusCode.NoContent && nombre == "Nombre Editado",
                        Estado(r, "204 y cambio guardado"));
            }),
            new("DELETE /api/estudiantes/{id} → 204 y luego 404", async () =>
            {
                var d = await h.DeleteAsync($"api/estudiantes/{id}");
                var g = await h.GetAsync($"api/estudiantes/{id}");
                return (d.StatusCode == HttpStatusCode.NoContent && g.StatusCode == HttpStatusCode.NotFound,
                        $"DELETE {(int)d.StatusCode}, GET posterior {(int)g.StatusCode}");
            })
        ];
    }

    // ---------- LIBROS ----------
    private static List<Paso> PasosLibros(HttpClient h)
    {
        long id = 0;
        var isbn = "ISBN-" + U();

        return
        [
            new("GET /api/libros → 200 con una lista", async () =>
            {
                var r = await h.GetAsync("api/libros");
                var j = await Leer(r);
                return (r.StatusCode == HttpStatusCode.OK && j?.ValueKind == JsonValueKind.Array,
                        Estado(r, "200 y una lista"));
            }),
            new("POST /api/libros → 201 y devuelve el id", async () =>
            {
                var r = await h.PostAsJsonAsync("api/libros", CuerpoLibro(isbn, 3));
                id = Id(await Leer(r), "idLibro");
                return (r.StatusCode == HttpStatusCode.Created && id > 0, Estado(r, "201") + $", id = {id}");
            }),
            new("GET /api/libros/{id} → 200 con los datos", async () =>
            {
                var r = await h.GetAsync($"api/libros/{id}");
                var ok = r.StatusCode == HttpStatusCode.OK && Texto(await Leer(r), "isbn") == isbn;
                return (ok, Estado(r, "200 con el mismo ISBN"));
            }),
            new("GET con id inexistente → 404", () => Codigo(h.GetAsync("api/libros/999999"), 404)),
            new("POST con ISBN repetido → 409",
                () => Codigo(h.PostAsJsonAsync("api/libros", CuerpoLibro(isbn, 1)), 409)),
            new("PUT /api/libros/{id} → 204 y guarda el cambio", async () =>
            {
                var r = await h.PutAsJsonAsync($"api/libros/{id}",
                    new
                    {
                        idLibro = id, titulo = "Título Editado", autor = "Autor de prueba", isbn,
                        categoria = "Prueba", cantidadDisponible = 3
                    });
                var titulo = Texto(await Leer(await h.GetAsync($"api/libros/{id}")), "titulo");
                return (r.StatusCode == HttpStatusCode.NoContent && titulo == "Título Editado",
                        Estado(r, "204 y cambio guardado"));
            }),
            new("DELETE /api/libros/{id} → 204 y luego 404", async () =>
            {
                var d = await h.DeleteAsync($"api/libros/{id}");
                var g = await h.GetAsync($"api/libros/{id}");
                return (d.StatusCode == HttpStatusCode.NoContent && g.StatusCode == HttpStatusCode.NotFound,
                        $"DELETE {(int)d.StatusCode}, GET posterior {(int)g.StatusCode}");
            })
        ];
    }

    // ---------- PRÉSTAMOS ----------
    private static List<Paso> PasosPrestamos(HttpClient h)
    {
        long idE = 0, idL = 0, id = 0;

        return
        [
            new("Preparación: crear un estudiante y un libro (1 ejemplar)", async () =>
            {
                idE = await NuevoEstudiante(h);
                idL = await NuevoLibro(h, 1);
                return (idE > 0 && idL > 0, $"estudiante = {idE}, libro = {idL}");
            }, Puntua: false),
            new("GET /api/prestamos → 200 con una lista", async () =>
            {
                var r = await h.GetAsync("api/prestamos");
                var j = await Leer(r);
                return (r.StatusCode == HttpStatusCode.OK && j?.ValueKind == JsonValueKind.Array,
                        Estado(r, "200 y una lista"));
            }),
            new("POST /api/prestamos → 201, estado Activo", async () =>
            {
                var r = await h.PostAsJsonAsync("api/prestamos",
                    new { idPrestamo = 0, idEstudiante = idE, idLibro = idL });
                var j = await Leer(r);
                id = Id(j, "idPrestamo");
                return (r.StatusCode == HttpStatusCode.Created && id > 0 && Texto(j, "estado") == "Activo",
                        Estado(r, "201 con estado Activo") + $", id = {id}");
            }),
            new("El préstamo descuenta 1 del inventario del libro", async () =>
            {
                var c = await Cantidad(h, idL);
                return (c == 0, $"Esperado 0 ejemplares, recibido {c}");
            }),
            new("GET /api/prestamos/{id} → 200", () => Codigo(h.GetAsync($"api/prestamos/{id}"), 200)),
            new("GET con id inexistente → 404", () => Codigo(h.GetAsync("api/prestamos/999999"), 404)),
            new("POST sin ejemplares disponibles → 400", () =>
                Codigo(h.PostAsJsonAsync("api/prestamos",
                    new { idPrestamo = 0, idEstudiante = idE, idLibro = idL }), 400)),
            new("POST con libro inexistente → 400", () =>
                Codigo(h.PostAsJsonAsync("api/prestamos",
                    new { idPrestamo = 0, idEstudiante = idE, idLibro = 999999 }), 400)),
            new("POST con estudiante inexistente → 400", () =>
                Codigo(h.PostAsJsonAsync("api/prestamos",
                    new { idPrestamo = 0, idEstudiante = 999999, idLibro = idL }), 400)),
            new("PUT /api/prestamos/{id} → 204 y extiende la fecha límite", async () =>
            {
                var nueva = DateTime.Now.AddDays(30);
                var r = await h.PutAsJsonAsync($"api/prestamos/{id}", new
                {
                    idPrestamo = id, idEstudiante = idE, idLibro = idL,
                    fechaLimite = nueva.ToString("yyyy-MM-dd HH:mm:ss")
                });
                var guardada = Texto(await Leer(await h.GetAsync($"api/prestamos/{id}")), "fechaLimite");
                var ok = r.StatusCode == HttpStatusCode.NoContent
                         && DateTime.TryParse(guardada, out var f) && f.Date == nueva.Date;
                return (ok, Estado(r, "204 y fecha guardada"));
            })
        ];
    }

    // ---------- DEVOLUCIONES ----------
    private static List<Paso> PasosDevoluciones(HttpClient h)
    {
        long idE = 0, idL = 0, idP = 0, id = 0;

        return
        [
            new("Preparación: crear estudiante, libro (1 ejemplar) y préstamo", async () =>
            {
                idE = await NuevoEstudiante(h);
                idL = await NuevoLibro(h, 1);
                idP = await NuevoPrestamo(h, idE, idL);
                return (idP > 0, $"estudiante = {idE}, libro = {idL}, préstamo = {idP}");
            }, Puntua: false),
            new("GET /api/devoluciones → 200 con una lista", async () =>
            {
                var r = await h.GetAsync("api/devoluciones");
                var j = await Leer(r);
                return (r.StatusCode == HttpStatusCode.OK && j?.ValueKind == JsonValueKind.Array,
                        Estado(r, "200 y una lista"));
            }),
            new("POST /api/devoluciones → 201, sin multa y estado Bueno", async () =>
            {
                var r = await h.PostAsJsonAsync("api/devoluciones", new
                {
                    idDevolucion = 0, idPrestamo = idP, estadoLibro = "Bueno", observaciones = "Sin daños"
                });
                var j = await Leer(r);
                id = Id(j, "idDevolucion");
                var sinMulta = j is { } e && e.TryGetProperty("multa", out var m) && m.GetDouble() == 0;
                return (r.StatusCode == HttpStatusCode.Created && id > 0 && sinMulta,
                        Estado(r, "201 con multa 0") + $", id = {id}");
            }),
            new("La devolución restaura el inventario del libro", async () =>
            {
                var c = await Cantidad(h, idL);
                return (c == 1, $"Esperado 1 ejemplar, recibido {c}");
            }),
            new("La devolución marca el préstamo como Devuelto", async () =>
            {
                var estado = Texto(await Leer(await h.GetAsync($"api/prestamos/{idP}")), "estado");
                return (estado == "Devuelto", $"Esperado Devuelto, recibido {estado}");
            }),
            new("GET /api/devoluciones/{id} → 200", () => Codigo(h.GetAsync($"api/devoluciones/{id}"), 200)),
            new("GET con id inexistente → 404", () => Codigo(h.GetAsync("api/devoluciones/999999"), 404)),
            new("POST de un préstamo ya devuelto → 400", () =>
                Codigo(h.PostAsJsonAsync("api/devoluciones",
                    new { idDevolucion = 0, idPrestamo = idP, estadoLibro = "Bueno" }), 400)),
            new("POST con préstamo inexistente → 400", () =>
                Codigo(h.PostAsJsonAsync("api/devoluciones",
                    new { idDevolucion = 0, idPrestamo = 999999, estadoLibro = "Bueno" }), 400)),
            new("PUT /api/devoluciones/{id} → 204 y guarda el cambio", async () =>
            {
                var r = await h.PutAsJsonAsync($"api/devoluciones/{id}", new
                {
                    idDevolucion = id, idPrestamo = idP, estadoLibro = "Dañado", observaciones = "Páginas rotas"
                });
                var estado = Texto(await Leer(await h.GetAsync($"api/devoluciones/{id}")), "estadoLibro");
                return (r.StatusCode == HttpStatusCode.NoContent && estado == "Dañado",
                        Estado(r, "204 y cambio guardado"));
            })
        ];
    }
}