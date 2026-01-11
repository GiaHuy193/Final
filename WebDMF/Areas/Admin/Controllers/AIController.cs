using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Text;
using UglyToad.PdfPig;
using WebDocumentManagement_FileSharing.Data;

namespace WebDocumentManagement_FileSharing.Controllers
{
    public class AIController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly string _apiKey;

        public AIController(ApplicationDbContext context, IWebHostEnvironment env, IConfiguration configuration)
        {
            _context = context;
            _env = env;
            _apiKey = configuration["Gemini:ApiKey"];
        }

        [HttpPost]
        public async Task<IActionResult> SummarizeDocument(int documentId)
        {
            if (string.IsNullOrEmpty(_apiKey)) return Json(new { success = false, error = "Chưa cấu hình API Key." });

            var doc = await _context.Documents.FindAsync(documentId);
            if (doc == null) return Json(new { success = false, error = "File không tồn tại." });

            string filePath = Path.Combine(_env.WebRootPath, doc.FilePath.TrimStart('/'));
            if (!System.IO.File.Exists(filePath)) return Json(new { success = false, error = "File vật lý không tìm thấy." });

            string ext = Path.GetExtension(filePath).ToLower();
            string fileContent = "";

            try
            {
                // --- XỬ LÝ VĂN BẢN / WORD / PDF / EXCEL ---
                
                // 1. File Text thuần túy
                if (ext == ".txt" || ext == ".md" || ext == ".cs" || ext == ".html")
                {
                    fileContent = await System.IO.File.ReadAllTextAsync(filePath);
                }
                // 2. File Word (.docx)
                else if (ext == ".docx")
                {
                    using (WordprocessingDocument wordDoc = WordprocessingDocument.Open(filePath, false))
                    {
                        fileContent = wordDoc.MainDocumentPart.Document.Body.InnerText;
                    }
                }
                // 3. File PDF (.pdf)
                else if (ext == ".pdf")
                {
                    using (PdfDocument pdf = PdfDocument.Open(filePath))
                    {
                        foreach (var page in pdf.GetPages())
                        {
                            fileContent += page.Text + " ";
                        }
                    }
                }
                // 4. File Excel (.xlsx)
                else if (ext == ".xlsx")
                {
                    using (SpreadsheetDocument spreadsheet = SpreadsheetDocument.Open(filePath, false))
                    {
                        WorkbookPart workbookPart = spreadsheet.WorkbookPart;
                        SharedStringTablePart sstpart = workbookPart.GetPartsOfType<SharedStringTablePart>().FirstOrDefault();
                        SharedStringTable sst = sstpart?.SharedStringTable;

                        StringBuilder excelText = new StringBuilder();

                        foreach (WorksheetPart worksheetPart in workbookPart.WorksheetParts)
                        {
                            Worksheet sheet = worksheetPart.Worksheet;
                            var rows = sheet.GetFirstChild<SheetData>().Elements<Row>();
                            foreach (var row in rows)
                            {
                                foreach (var cell in row.Elements<Cell>())
                                {
                                    string cellValue = "";
                                    if (cell.DataType != null && cell.DataType == CellValues.SharedString)
                                    {
                                        if (cell.CellValue != null)
                                        {
                                            int ssid = int.Parse(cell.CellValue.Text);
                                            if (sst != null && sst.ChildElements.Count > ssid)
                                            {
                                                cellValue = sst.ChildElements[ssid].InnerText;
                                            }
                                        }
                                    }
                                    else if (cell.CellValue != null)
                                    {
                                        cellValue = cell.CellValue.Text;
                                    }
                                    excelText.Append(cellValue + " | ");
                                }
                                excelText.AppendLine();
                            }
                            excelText.AppendLine("--- End of Sheet ---");
                        }
                        fileContent = excelText.ToString();
                    }
                }
                else
                {
                    return Json(new { success = false, error = "AI chưa hỗ trợ định dạng file này." });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = $"Lỗi đọc file: {ex.Message}" });
            }

            if (string.IsNullOrWhiteSpace(fileContent))
                return Json(new { success = false, error = "File không có nội dung chữ có thể đọc được." });

            // Cắt ngắn nếu quá dài để tránh lỗi quá tải token
            if (fileContent.Length > 100000) fileContent = fileContent.Substring(0, 100000);

            // Gọi Gemini
            var textSummary = await CallGeminiApi(fileContent);
            return Json(new { success = true, summary = textSummary });
        }

        // --- GỌI GEMINI API ---
        private async Task<string> CallGeminiApi(string textToSummarize)
        {
            using (var client = new HttpClient())
            {
                // Sử dụng model Gemini 2.5 Flash
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={_apiKey}";

                var requestBody = new
                {
                    contents = new[]
                    {
                        new {
                            parts = new[] {
                                // Prompt cho phép trả về Markdown (để thư viện marked.js ở frontend xử lý)
                                new { text = $"Bạn là trợ lý AI. Hãy tóm tắt tài liệu sau bằng tiếng Việt. Yêu cầu: Ngắn gọn, súc tích, gạch đầu dòng các ý chính:\n\n{textToSummarize}" }
                            }
                        }
                    }
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                try
                {
                    var response = await client.PostAsync(url, content);
                    var responseString = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        dynamic result = JsonConvert.DeserializeObject(responseString);
                        if (result.candidates != null && result.candidates.Count > 0)
                            return result.candidates[0].content.parts[0].text;
                        return "AI không trả về kết quả.";
                    }
                    else
                    {
                        return $"Lỗi API: {response.StatusCode} - {responseString}";
                    }
                }
                catch (Exception ex) { return "Lỗi kết nối: " + ex.Message; }
            }
        }
    }
}