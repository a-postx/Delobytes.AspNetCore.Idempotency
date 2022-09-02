using System;
using System.Collections.Generic;

namespace Delobytes.AspNetCore.Idempotency;

/// <summary>
/// Модель веб-запроса и результата.
/// </summary>
[Serializable]
public class ApiRequest
{
    /// <summary>
    /// Конструктор.
    /// </summary>
    public ApiRequest(string apiRequestId, string method)
    {
        ApiRequestID = apiRequestId;
        Method = method;
    }

    /// <summary>
    /// Уникальный идентификатор запроса, определяемый ключом идемпотентности.
    /// </summary>
    public string ApiRequestID { get; set; }
    /// <summary>
    /// Метод запроса.
    /// </summary>
    public string Method { get; set; }
    /// <summary>
    /// Путь запроса.
    /// </summary>
    public string? Path { get; set; }
    /// <summary>
    /// Параметры запроса.
    /// </summary>
    public string? Query { get; set; }
    /// <summary>
    /// Код ответа.
    /// </summary>
    public int? StatusCode { get; set; }
    /// <summary>
    /// Заголовки ответа.
    /// </summary>
    public Dictionary<string, List<string>>? Headers { get; set; }
    /// <summary>
    /// Тип объекта, который сериализован в тело ответа.
    /// </summary>
    public string? BodyType { get; set; }
    /// <summary>
    /// Тело ответа.
    /// </summary>
    public byte[]? Body { get; set; }
    /// <summary>
    /// Тип результата запроса (IActionResult).
    /// </summary>
    public string? ResultType { get; set; }
    /// <summary>
    /// Название маршрута, по которому сгенерирован URL в случае создания объекта (CreatedAtRouteResult).
    /// </summary>
    public string? ResultRouteName { get; set; }
    /// <summary>
    /// Данные, с помощью которых построен результирующий маршрут (CreatedAtRouteResult).
    /// </summary>
    public Dictionary<string, string?>? ResultRouteValues { get; set; }
}
