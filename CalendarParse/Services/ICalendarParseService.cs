using CalendarParse.Models;

namespace CalendarParse.Services
{
    public interface ICalendarParseService
    {
        /// <summary>
        /// Full pipeline: preprocess → detect table → OCR → analyze → serialize.
        /// </summary>
        /// <param name="imageStream">Raw image bytes from camera or gallery.</param>
        /// <param name="nameFilter">If non-empty, result only includes the matching employee row.</param>
        /// <returns>JSON string of <see cref="CalendarData"/>, or an error message prefixed with "ERROR:".</returns>
        Task<string> ProcessAsync(Stream imageStream, string nameFilter, CancellationToken ct = default);
    }
}
