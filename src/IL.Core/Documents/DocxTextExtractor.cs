using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
namespace IL.Core.Documents;
public sealed record DocxTextResult(string Text,int ParagraphCount,int TableCount);
public sealed class DocxTextExtractionException(string message,Exception? inner=null):Exception(message,inner);
public sealed class DocxTextExtractor
{
    private sealed record Reference(int NumId,int Level);
    private sealed record Level(int Start,string Format,string Template);
    private static IEnumerable<XElement> Direct(XElement e,string name)=>e.Elements().Where(x=>x.Name.LocalName==name);
    private static XElement? Child(XElement? e,string name)=>e?.Elements().FirstOrDefault(x=>x.Name.LocalName==name);
    private static string? Attr(XElement? e,string name="val")=>e?.Attributes().FirstOrDefault(a=>a.Name.LocalName==name)?.Value;
    private static Reference? Numbering(XElement? properties)
    {
        var number=Child(properties,"numPr");return int.TryParse(Attr(Child(number,"numId")),out var id)&&id>0?new(id,int.TryParse(Attr(Child(number,"ilvl")),out var level)?level:0):null;
    }
    public async Task<DocxTextResult> ExtractFileAsync(string file,CancellationToken ct=default)
    {if(!File.Exists(file))throw new DocxTextExtractionException("找不到指定 DOCX 文件");return ExtractBytes(await File.ReadAllBytesAsync(file,ct));}
    public DocxTextResult ExtractBytes(byte[] bytes)
    {
        try
        {
            using var stream=new MemoryStream(bytes);using var archive=new ZipArchive(stream,ZipArchiveMode.Read);
            XDocument? Read(string name){var entry=archive.GetEntry(name);if(entry==null)return null;using var file=entry.Open();return XDocument.Load(file);}
            XDocument document;try{document=Read("word/document.xml")??throw new DocxTextExtractionException("DOCX 中缺少正文内容");}catch(System.Xml.XmlException ex){throw new DocxTextExtractionException("DOCX 正文无法解析",ex);}
            var body=document.Descendants().FirstOrDefault(e=>e.Name.LocalName=="body")??throw new DocxTextExtractionException("DOCX 中没有可读取的正文");
            var schemes=new Dictionary<int,Dictionary<int,Level>>();var styles=new Dictionary<string,(string? BasedOn,Reference? Numbering)>();
            try
            {
                var numberDoc=Read("word/numbering.xml");var abstracts=new Dictionary<int,Dictionary<int,Level>>();
                foreach(var a in numberDoc?.Descendants().Where(e=>e.Name.LocalName=="abstractNum")??[])
                {
                    if(!int.TryParse(Attr(a,"abstractNumId"),out var id))continue;var levels=new Dictionary<int,Level>();
                    foreach(var l in Direct(a,"lvl"))if(int.TryParse(Attr(l,"ilvl"),out var i))levels[i]=new(int.TryParse(Attr(Child(l,"start")),out var start)?start:1,Attr(Child(l,"numFmt"))??"decimal",Attr(Child(l,"lvlText"))??$"%{i+1}.");
                    abstracts[id]=levels;
                }
                foreach(var number in numberDoc?.Descendants().Where(e=>e.Name.LocalName=="num")??[])
                    if(int.TryParse(Attr(number,"numId"),out var id)&&int.TryParse(Attr(Child(number,"abstractNumId")),out var aid)&&abstracts.TryGetValue(aid,out var levels))schemes[id]=levels;
            }
            catch(System.Xml.XmlException){}
            try{foreach(var s in Read("word/styles.xml")?.Descendants().Where(e=>e.Name.LocalName=="style")??[])if(Attr(s,"styleId") is {} id)styles[id]=(Attr(Child(s,"basedOn")),Numbering(Child(s,"pPr")));}catch(System.Xml.XmlException){}
            Reference? StyleReference(string? id,HashSet<string> visiting){if(id==null||!visiting.Add(id)||!styles.TryGetValue(id,out var style))return null;return style.Numbering??StyleReference(style.BasedOn,visiting);}
            var counters=new Dictionary<int,Dictionary<int,int>>();
            string NextLabel(Reference reference)
            {
                if(!schemes.TryGetValue(reference.NumId,out var levels))return "";var level=levels.GetValueOrDefault(reference.Level)??new Level(1,"decimal",$"%{reference.Level+1}.");
                if(!counters.TryGetValue(reference.NumId,out var counts))counters[reference.NumId]=counts=[];
                foreach(var index in counts.Keys.Where(i=>i>reference.Level).ToArray())counts.Remove(index);
                counts[reference.Level]=counts.GetValueOrDefault(reference.Level,level.Start-1)+1;
                var label=level.Template;foreach(var (index,value) in counts)label=label.Replace($"%{index+1}",FormatNumber(value,levels.GetValueOrDefault(index)?.Format??"decimal"));return label.Trim();
            }
            void Append(XElement e,StringBuilder output)
            {
                foreach(var child in e.Elements())switch(child.Name.LocalName)
                {
                    case "del":case "pPr":break;case "t":output.Append(child.Value);break;case "tab":output.Append('\t');break;
                    case "br":case "cr":output.Append('\n');break;case "noBreakHyphen":output.Append('\u2011');break;default:Append(child,output);break;
                }
            }
            string Paragraph(XElement p)
            {
                var output=new StringBuilder();var props=Child(p,"pPr");var reference=Numbering(props)??StyleReference(Attr(Child(props,"pStyle")),[]);
                if(reference!=null){var label=NextLabel(reference);if(label.Length>0)output.Append(label).Append(' ');}Append(p,output);return Regex.Replace(output.ToString(),@"[ \t]+\n","\n").Trim();
            }
            var paragraphs=0;var tables=0;
            string Table(XElement table)
            {
                var rows=new List<string>();foreach(var row in Direct(table,"tr"))
                {
                    var cells=new List<string>();foreach(var cell in Direct(row,"tc"))
                    {
                        var blocks=new List<string>();foreach(var child in cell.Elements())
                        {if(child.Name.LocalName=="p"){paragraphs++;var text=Paragraph(child);if(text.Length>0)blocks.Add(text);}else if(child.Name.LocalName=="tbl"){var nested=Table(child);if(nested.Length>0)blocks.Add(nested);}}
                        cells.Add(string.Join(" / ",blocks));
                    }
                    if(cells.Any(c=>c.Length>0))rows.Add(string.Join('\t',cells));
                }
                return string.Join('\n',rows);
            }
            var blocks=new List<string>();foreach(var child in body.Elements())
            {if(child.Name.LocalName=="p"){paragraphs++;var text=Paragraph(child);if(text.Length>0)blocks.Add(text);}else if(child.Name.LocalName=="tbl"){tables++;var text=Table(child);if(text.Length>0)blocks.Add(text);}}
            var output=Regex.Replace(string.Join('\n',blocks).Replace("\r\n","\n").Replace('\r','\n'),@"[ \t]+$","",RegexOptions.Multiline).Trim();output=Regex.Replace(output,@"\n[ \t]*\n(?:[ \t]*\n)+","\n\n");
            if(output.Length==0)throw new DocxTextExtractionException("DOCX 中没有可提取的文字");return new(output,paragraphs,tables);
        }
        catch(InvalidDataException ex){throw new DocxTextExtractionException("DOCX 文件已损坏或格式无效",ex);}
    }
    private static string FormatNumber(int value,string format)
    {
        string Letters(){var current=value;var buffer="";while(current>0){current--;buffer=(char)('A'+current%26)+buffer;current/=26;}return buffer;}
        string Roman(){if(value<=0||value>3999)return value.ToString();var remaining=value;var result=new StringBuilder();foreach(var (n,s) in new (int,string)[]{(1000,"M"),(900,"CM"),(500,"D"),(400,"CD"),(100,"C"),(90,"XC"),(50,"L"),(40,"XL"),(10,"X"),(9,"IX"),(5,"V"),(4,"IV"),(1,"I")})while(remaining>=n){result.Append(s);remaining-=n;}return result.ToString();}
        return format switch{"lowerLetter"=>Letters().ToLowerInvariant(),"upperLetter"=>Letters(),"lowerRoman"=>Roman().ToLowerInvariant(),"upperRoman"=>Roman(),_=>value.ToString()};
    }
}
