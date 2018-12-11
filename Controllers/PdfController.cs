using Korazon.PdfGenerator.Filters;
using Korazon.PdfGenerator.Services;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web.Http;

namespace Korazon.PdfGenerator.Controllers
{
    [HMACAuthentication]
    public class PdfController : ApiController
    {
        private readonly PdfServiceV2 _pdfService;
        private readonly Logger _logger;

        public PdfController()
        {
            _pdfService = new PdfServiceV2();
            _logger = LogManager.GetCurrentClassLogger();
        }

        // GET api/pdf/5/userid-guid
        [Route("api/pdf/{id}/{userId}")]
        [HttpGet]
        public HttpResponseMessage Get(int id, string userId)
        {
            _logger.Info($"id = {id} - userId = {userId}");

            try
            {
                var fullPath = _pdfService.GetCreatedDocumentPath(id, userId);

                if (fullPath == string.Empty)
                    return Request.CreateResponse(HttpStatusCode.NotFound);

                if (!File.Exists(fullPath))
                    _pdfService.CreatePdfDocument(id, userId);

                var statuscode = HttpStatusCode.OK;
                var response = Request.CreateResponse(statuscode);
                var fileStream = new FileStream(fullPath, FileMode.Open);
                var contentLength = fileStream.Length;
                response.Content = new StreamContent(fileStream);
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                response.Content.Headers.ContentLength = contentLength;
                ContentDispositionHeaderValue contentDisposition = null;
                if (ContentDispositionHeaderValue.TryParse("inline; filename=" + Path.GetFileName(fullPath), out contentDisposition))
                {
                    response.Content.Headers.ContentDisposition = contentDisposition;
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger.Error(ex);
                return Request.CreateResponse(HttpStatusCode.BadRequest, ex.Message);
            }
        }
    }
}
