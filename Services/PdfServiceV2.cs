using NLog;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;
using iText.Kernel.Pdf;
using iText.Forms;
using iText.Forms.Fields;
using System.Data.Entity;
using System;
using iText.IO.Image;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Renderer;
using iText.Layout.Layout;
using Korazon.PdfGenerator.Properties;
using Korazon.PdfGenerator.Data.Models;

namespace Korazon.PdfGenerator.Services
{
    public class PdfServiceV2
    {
        private ApplicationDbContext _db;
        private readonly string _basePathTemplates;
        private readonly string _basePathFotos;
        private readonly string _basePathUserSavedPdfs;
        private readonly Logger _logger;

        public PdfServiceV2()
        {
            _basePathTemplates = Path.Combine(HttpContext.Current.Server.MapPath("/"), "assets", "pdf");
            _basePathFotos = Settings.Default.basePathFotos;
            _basePathUserSavedPdfs = Settings.Default.basePathUserSavedPdfs;
            _logger = LogManager.GetCurrentClassLogger();
            _db = new ApplicationDbContext();
        }

        public void CreatePdfDocument(int id, string userId)
        {
            var dbDoc = _db.CreatedPdfDocumenten
                .Include(x => x.DocumentPartValues)
                    .Include(x => x.DocumentPartValues.Select(p => p.PdfDocumentPart))
                .Include(x => x.PdfDocument)
                .Include(x => x.UserInsert)
                .Include(x => x.Logo)
                .First(x => x.Id == id && x.UserInsert.Id == userId);

            var src = Path.Combine(_basePathTemplates, dbDoc.PdfDocument.FileName);
            var destination = Path.Combine(_basePathUserSavedPdfs, dbDoc.UserInsert.Email, dbDoc.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));

            iText.Kernel.Pdf.PdfDocument pdfDoc = new iText.Kernel.Pdf.PdfDocument(new PdfReader(src), new PdfWriter(destination));

            PdfAcroForm form = PdfAcroForm.GetAcroForm(pdfDoc, true);
            var fields = form.GetFormFields();
            var values = dbDoc.DocumentPartValues.Select(x => new { x.Value, x.PdfDocumentPart.VeldNaam, x.PdfDocumentPart.Pagina });
            var taalConfiguraties = _db.PdfConfiguraties.Where(x => x.Language == dbDoc.Taal).OrderBy(x => x.Key).ToList();

            foreach (var field in fields)
            {
                var v = values.FirstOrDefault(va => va.VeldNaam == field.Key);
                PdfFormField toSet;
                fields.TryGetValue(field.Key, out toSet);

                // logo velden
                if (field.Key.ToLower().Contains("logo"))
                {
                    if (toSet is PdfButtonFormField)
                    {
                        SetLogo(toSet, pdfDoc, Path.Combine(_basePathTemplates, dbDoc.Logo.FileName), field.Key.ToLower() == "logo voorzijde" ? 1 : 2);
                    }
                    else
                    {
                        // zou niet mogen maar anders komt er een fout bij flattenfields
                        toSet.SetValue(field.Key);
                    }
                    continue;
                }

                // taalvelden
                if (field.Key.ToLower().Contains("footer") || field.Key.ToLower().Contains("afsluiting achterzijde"))
                {
                    SetFooter(toSet, pdfDoc, 2, taalConfiguraties);
                    continue;
                }

                // gewone velden
                if (v != null && v.Value != null)
                {
                    if (toSet is PdfButtonFormField)
                    {
                        var foto = _db.Fotos.First(f => f.Id.ToString() == v.Value);
                        var filename = Path.Combine(_basePathFotos, foto.Path, foto.Name);

                        SetImage(toSet, pdfDoc, filename, v.Pagina);
                    }
                    else
                    {
                        // rekening houden met maxlen
                        var tekst = GetTekstBeperktOpMaxLen(toSet, v.Value);
                        toSet.SetValue(tekst);
                    }
                }
                else
                {
                    toSet.SetValue("");
                }
            }
            form.FlattenFields();
            pdfDoc.Close();

            //dbDoc.IsCreated = true;
            //_db.SaveChanges();
        }

        public string GetCreatedDocumentPath(int id, string userId)
        {
            var dbDoc = _db.CreatedPdfDocumenten.FirstOrDefault(x => x.Id == id && x.UserInsert.Id == userId);
            if (dbDoc == null)
                return string.Empty;
            return Path.Combine(_basePathUserSavedPdfs, dbDoc.UserInsert.Email, dbDoc.FileName);
        }


        private string GetTekstBeperktOpMaxLen(PdfFormField toSet, string tekst)
        {
            var length = tekst.Length;
            var dict = toSet.GetPdfObject();
            var pdfName = new PdfName("MaxLen");
            if (dict.ContainsKey(pdfName))
                length = dict.GetAsInt(pdfName).Value;
            if (length > tekst.Length)
                return tekst;
            return tekst.Substring(0, length);
        }

        private void SetImage(PdfFormField toSet, iText.Kernel.Pdf.PdfDocument pdfDoc, string filename, int pagina)
        {
            var b = toSet as PdfButtonFormField;
            var afmetingen = b.GetWidgets().SelectMany(f => f.GetRectangle()).ToArray();
            var x = (int)Convert.ToDouble(afmetingen[0].ToString().Replace(".", ","));
            var y = (int)Convert.ToDouble(afmetingen[1].ToString().Replace(".", ","));
            var wWidth = (int)Convert.ToDouble(afmetingen[2].ToString().Replace(".", ","));
            var wHeigth = (int)Convert.ToDouble(afmetingen[3].ToString().Replace(".", ","));

            ImageData img = ImageDataFactory.Create(filename);
            var pdfImage = new iText.Layout.Element.Image(img);
            var scaled = pdfImage.ScaleToFit(wWidth, wHeigth - y);
            var scaledWidth = scaled.GetImageScaledWidth();
            var scaledHeight = scaled.GetImageScaledHeight();
            Document d = new Document(pdfDoc);

            var berekendeX = (x + wWidth - scaledWidth) / 2;
            var berekendeY = (y + wHeigth - scaledHeight) / 2;
            scaled.SetFixedPosition(pagina, berekendeX, berekendeY);
            d.Add(scaled);
            b.SetValue("");
        }

        private void SetFooter(PdfFormField toSet, iText.Kernel.Pdf.PdfDocument pdfDoc, int pagina, List<PdfConfiguratie> footerTeksten)
        {
            toSet.SetValue("");

            var afmetingen = toSet.GetWidgets().SelectMany(f => f.GetRectangle()).ToArray();
            var x = (int)Convert.ToDouble(afmetingen[0].ToString().Replace(".", ","));
            var y = (int)Convert.ToDouble(afmetingen[1].ToString().Replace(".", ","));
            var wWidth = (int)Convert.ToDouble(afmetingen[2].ToString().Replace(".", ","));
            var wHeigth = (int)Convert.ToDouble(afmetingen[3].ToString().Replace(".", ","));

            Document d = new Document(pdfDoc);
            var footerTekst = footerTeksten.FirstOrDefault();
            if (footerTekst == null)
                return;
            var paragraaf = new Paragraph(footerTekst.Value);
            paragraaf.SetFontSize(14);
            var styleBold = new Style();
            styleBold.SetBold();
            paragraaf.AddStyle(styleBold);
            var bottom = y + wHeigth - GetParagraafHeight(paragraaf, d, wWidth) * 2;
            paragraaf.SetFixedPosition(pagina, x, bottom, wWidth);
            d.Add(paragraaf);

            footerTekst = footerTeksten.Skip(1).FirstOrDefault();
            if (footerTekst == null)
                return;
            var paragraaf2 = new Paragraph(footerTekst.Value);
            paragraaf2.SetFontSize(12);
            bottom -= GetParagraafHeight(paragraaf2, d, wWidth);
            paragraaf2.SetFixedPosition(pagina, x, bottom, wWidth);
            d.Add(paragraaf2);

            footerTekst = footerTeksten.Skip(2).FirstOrDefault();
            if (footerTekst == null)
                return;
            var paragraaf3 = new Paragraph(footerTekst.Value);
            paragraaf3.SetFontSize(14);
            bottom -= GetParagraafHeight(paragraaf3, d, wWidth);
            paragraaf3.SetFixedPosition(pagina, x, bottom, wWidth);
            d.Add(paragraaf3);
        }

        private float GetParagraafHeight(Paragraph paragraaf, Document doc, int width)
        {
            // Create renderer tree
            IRenderer paragraphRenderer = paragraaf.CreateRendererSubTree();
            // Do not forget setParent(). Set the dimensions of the viewport as needed
            LayoutResult layoutResult = paragraphRenderer
                .SetParent(doc.GetRenderer())
                .Layout(new LayoutContext(new LayoutArea(1, new iText.Kernel.Geom.Rectangle(width, 1000))));

            // LayoutResult#getOccupiedArea() contains the information you need
            return layoutResult.GetOccupiedArea().GetBBox().GetHeight();
        }

        private void SetLogo(PdfFormField toSet, iText.Kernel.Pdf.PdfDocument pdfDoc, string filename, int pagina)
        {
            var b = toSet as PdfButtonFormField;
            var afmetingen = b.GetWidgets().SelectMany(f => f.GetRectangle()).ToArray();
            var x = (int)Convert.ToDouble(afmetingen[0].ToString().Replace(".", ","));
            if (x < 10)
                x = 100;
            var y = (int)Convert.ToDouble(afmetingen[1].ToString().Replace(".", ","));
            var wWidth = (int)Convert.ToDouble(afmetingen[2].ToString().Replace(".", ","));
            var pageWidth = (int)pdfDoc.GetPage(1).GetPageSizeWithRotation().GetWidth();
            if (wWidth > pageWidth - 20)
                wWidth = pageWidth - 20;
            var wHeight = (int)Convert.ToDouble(afmetingen[3].ToString().Replace(".", ","));
            if (pagina == 1)
                wHeight -= 10;

            ImageData img = ImageDataFactory.Create(filename);
            var pdfImage = new iText.Layout.Element.Image(img);
            var scaled = pdfImage.ScaleToFit(wWidth, wHeight - y);
            var scaledWidth = scaled.GetImageScaledWidth();
            var scaledHeight = scaled.GetImageScaledHeight();
            Document d = new Document(pdfDoc);

            var berekendeX = (x + wWidth - scaledWidth) / 2;
            var berekendeY = (y + wHeight - scaledHeight) / 2;
            scaled.SetFixedPosition(pagina, berekendeX, berekendeY);
            d.Add(scaled);
            b.SetValue("");

        }
    }
}