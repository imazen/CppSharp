using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CppSharp.AST;
using CppSharp.Types;

#if !OLD_PARSER
using CppAbi = CppSharp.Parser.AST.CppAbi;
#endif

namespace CppSharp.Passes
{
    public class GenerateInlinesCodePass : TranslationUnitPass
    {
        private TranslationUnit currentUnit;
        private readonly List<string> headers = new List<string>();
        private readonly List<string> templates = new List<string>(); 
        private readonly List<string> mangledInlines = new List<string>(); 

        public override bool VisitLibrary(ASTContext context)
        {
            bool result = base.VisitLibrary(context);
            Directory.CreateDirectory(Driver.Options.OutputDir);
            WriteInlinesIncludes();
            WriteInlinedSymbols();
            return result;
        }

        private void WriteInlinesIncludes()
        {
            var cppBuilder = new StringBuilder();
            headers.Sort();
            foreach (var header in headers)
                cppBuilder.AppendFormat("#include \"{0}\"\n", header);
            cppBuilder.AppendLine();
            foreach (var template in templates)
                cppBuilder.AppendFormat("template class __declspec(dllexport) {0};\n", template);
            var cpp = string.Format("{0}.cpp", Driver.Options.InlinesLibraryName);
            var path = Path.Combine(Driver.Options.OutputDir, cpp);
            File.WriteAllText(path, cppBuilder.ToString());
        }

        private void WriteInlinedSymbols()
        {
            switch (Driver.Options.Abi)
            {
                case CppAbi.Microsoft:
                    var defBuilder = new StringBuilder("EXPORTS\r\n");
                    for (int i = 0; i < mangledInlines.Count; i++)
                        defBuilder.AppendFormat("    {0} @{1}\r\n",
                                                mangledInlines[i], i + 1);
                    var def = string.Format("{0}.def", Driver.Options.InlinesLibraryName);
                    File.WriteAllText(Path.Combine(Driver.Options.OutputDir, def),
                                      defBuilder.ToString());
                    break;
                default:
                    var symbolsBuilder = new StringBuilder();
                    foreach (var mangledInline in mangledInlines)
                        symbolsBuilder.AppendFormat("{0}\n", mangledInline);
                    var txt = string.Format("{0}.txt", Driver.Options.InlinesLibraryName);
                    File.WriteAllText(Path.Combine(Driver.Options.OutputDir, txt),
                                      symbolsBuilder.ToString());
                    break;
            }
        }

        public override bool VisitTranslationUnit(TranslationUnit unit)
        {
            currentUnit = unit;
            return base.VisitTranslationUnit(unit);
        }

        public override bool VisitTemplateSpecializationType(TemplateSpecializationType template, TypeQualifiers quals)
        {
            if (AlreadyVisited(template))
                return false;

            if (AreTemplateArgumentsValid(template.Arguments))
            {
                string typeString = new CppTypePrinter(Driver.TypeDatabase).VisitTemplateSpecializationType(template, quals);
                if (!templates.Contains(typeString))
                {
                    templates.Add(typeString);
                    if (!currentUnit.FilePath.EndsWith("_impl.h") &&
                        !currentUnit.FilePath.EndsWith("_p.h") &&
                        !headers.Contains(currentUnit.FileName))
                        headers.Add(currentUnit.FileName);
                    headers.AddRange(
                        from argument in template.Arguments
                        where argument.Declaration != null
                        let header = argument.Declaration.Namespace.TranslationUnit.FileName
                        where !headers.Contains(header)
                        select header);
                }
            }
            return base.VisitTemplateSpecializationType(template, quals);
        }

        private static bool AreTemplateArgumentsValid(IEnumerable<TemplateArgument> templateArguments)
        {
            foreach (var templateArgument in templateArguments)
            {
                if (templateArgument.Type.Type == null ||
                    templateArgument.Type.Type is DependentNameType ||
                    templateArgument.Type.Type is TemplateParameterType ||
                    (templateArgument.Declaration != null && (templateArgument.Declaration.Ignore ||
                     templateArgument.Declaration.Access == AccessSpecifier.Private)))
                    return false;
                var templateSpecializationType = templateArgument.Type.Type as TemplateSpecializationType;
                if (templateSpecializationType != null &&
                    !AreTemplateArgumentsValid(templateSpecializationType.Arguments))
                    return false;
            }
            return true;
        }

        public override bool VisitFunctionDecl(Function function)
        {
            CheckForSymbols(function);
            return base.VisitFunctionDecl(function);
        }

        public override bool VisitVariableDecl(Variable variable)
        {
            CheckForSymbols(variable);
            return base.VisitVariableDecl(variable);
        }

        private void CheckForSymbols(IMangledDecl mangled)
        {
            string symbol = mangled.Mangled;
            var declaration = (Declaration) mangled;
            if (!declaration.Ignore && AccessValid(declaration) &&
                !Driver.Symbols.FindSymbol(ref symbol) &&
                !currentUnit.FilePath.EndsWith("_impl.h") &&
                !currentUnit.FilePath.EndsWith("_p.h"))
            {
                if (!headers.Contains(currentUnit.FileName))
                    headers.Add(currentUnit.FileName);
                if (!mangledInlines.Contains(mangled.Mangled))
                    mangledInlines.Add(mangled.Mangled);
            }
        }

        private static bool AccessValid(Declaration declaration)
        {
            if (declaration.Access == AccessSpecifier.Private)
            {
                var method = declaration as Method;
                return method != null && method.IsOverride;
            }
            return true;
        }
    }
}
