using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Palmmedia.ReportGenerator.Core.Common;
using Palmmedia.ReportGenerator.Core.Parser.Analysis;
using Palmmedia.ReportGenerator.Core.Parser.Filtering;

namespace Palmmedia.ReportGenerator.Core.Parser
{
    /// <summary>
    /// Parser for reports generated by lcov (See: https://github.com/linux-test-project/lcov, http://ltp.sourceforge.net/coverage/lcov/geninfo.1.php).
    /// </summary>
    internal class LCovParser : ParserBase
    {
        /// <summary>
        /// The default assembly name.
        /// </summary>
        private readonly string defaultAssemblyName = "Default";

        /// <summary>
        /// Initializes a new instance of the <see cref="LCovParser" /> class.
        /// </summary>
        /// <param name="assemblyFilter">The assembly filter.</param>
        /// <param name="classFilter">The class filter.</param>
        /// <param name="fileFilter">The file filter.</param>
        public LCovParser(IFilter assemblyFilter, IFilter classFilter, IFilter fileFilter)
            : base(assemblyFilter, classFilter, fileFilter)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LCovParser" /> class.
        /// </summary>
        /// <param name="assemblyFilter">The assembly filter.</param>
        /// <param name="classFilter">The class filter.</param>
        /// <param name="fileFilter">The file filter.</param>
        /// <param name="defaultAssemblyName">The default assembly name.</param>
        public LCovParser(IFilter assemblyFilter, IFilter classFilter, IFilter fileFilter, string defaultAssemblyName)
            : base(assemblyFilter, classFilter, fileFilter)
        {
            this.defaultAssemblyName = defaultAssemblyName;
        }

        /// <summary>
        /// Parses the given report.
        /// </summary>
        /// <param name="lines">The report lines.</param>
        /// <returns>The parser result.</returns>
        public ParserResult Parse(string[] lines)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            var assembly = new Assembly(this.defaultAssemblyName);
            var assemblies = new List<Assembly>()
            {
                assembly
            };

            this.ProcessAssembly(assembly, lines);

            // Not every tool that generates LCov files creates branch coverage
            bool supportsBranchCoverage = assembly.Classes.Any(c => c.TotalBranches.GetValueOrDefault() > 0);
            var result = new ParserResult(assemblies, supportsBranchCoverage, this.ToString());
            return result;
        }

        private void ProcessAssembly(Assembly assembly, string[] lines)
        {
            var classesByPath = new Dictionary<string, Class>();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (line.StartsWith("SF:"))
                {
                    string fileName = line.Substring(line.IndexOf(":") + 1);

                    if (!fileName.StartsWith("http://") && !fileName.StartsWith("https://"))
                    {
                        fileName = fileName
                            .Replace('\\', Path.DirectorySeparatorChar)
                            .Replace('/', Path.DirectorySeparatorChar);
                    }

                    if (!this.FileFilter.IsElementIncludedInReport(fileName))
                    {
                        continue;
                    }

                    string className = fileName.Substring(fileName.LastIndexOf(Path.DirectorySeparatorChar) + 1);

                    if (!this.ClassFilter.IsElementIncludedInReport(className))
                    {
                        continue;
                    }

                    var @class = new Class(className, assembly);

                    this.ProcessClass(@class, fileName, lines, ref i);

                    if (classesByPath.TryGetValue(fileName, out Class existingClass))
                    {
                        existingClass.Merge(@class);
                    }
                    else
                    {
                        assembly.AddClass(@class);
                        classesByPath.Add(fileName, @class);
                    }
                }
            }
        }

        private void ProcessClass(Class @class, string fileName, string[] lines, ref int currentLine)
        {
            var codeElements = new List<CodeElementBase>();
            int maxiumLineNumber = -1;
            var visitsByLine = new Dictionary<int, int>();

            var branchesByLineNumber = new Dictionary<int, ICollection<Branch>>();

            while (true)
            {
                string line = lines[currentLine];

                if (line == "end_of_record")
                {
                    break;
                }

                currentLine++;

                if (line.StartsWith("FN:"))
                {
                    line = line.Substring(3);
                    int lineNumber = int.Parse(line.Substring(0, line.IndexOf(',')), CultureInfo.InvariantCulture);
                    string name = line.Substring(line.IndexOf(',') + 1);
                    codeElements.Add(new CodeElementBase(name, lineNumber));
                }
                else if (line.StartsWith("BRDA:"))
                {
                    line = line.Substring(5);
                    string[] tokens = line.Split(',');
                    int lineNumber = int.Parse(tokens[0], CultureInfo.InvariantCulture);

                    var branch = new Branch(
                        "-".Equals(tokens[3]) ? 0 : int.Parse(tokens[3], CultureInfo.InvariantCulture),
                        $"{tokens[0]}_{tokens[1]}_{tokens[2]}");

                    ICollection<Branch> branches = null;
                    if (branchesByLineNumber.TryGetValue(lineNumber, out branches))
                    {
                        HashSet<Branch> branchesHashset = (HashSet<Branch>)branches;
                        if (branchesHashset.Contains(branch))
                        {
                            // Not perfect for performance, but Hashset has no GetElement method
                            branchesHashset.First(b => b.Equals(branch)).BranchVisits += branch.BranchVisits;
                        }
                        else
                        {
                            branches.Add(branch);
                        }
                    }
                    else
                    {
                        branches = new HashSet<Branch>();
                        branches.Add(branch);

                        branchesByLineNumber.Add(lineNumber, branches);
                    }
                }
                else if (line.StartsWith("DA:"))
                {
                    line = line.Substring(3);
                    int lineNumber = int.Parse(line.Substring(0, line.IndexOf(',')), CultureInfo.InvariantCulture);
                    int visits = line.Substring(line.IndexOf(',') + 1).ParseLargeInteger();

                    maxiumLineNumber = Math.Max(maxiumLineNumber, lineNumber);

                    if (visitsByLine.ContainsKey(lineNumber))
                    {
                        visitsByLine[lineNumber] += visits;
                    }
                    else
                    {
                        visitsByLine[lineNumber] = visits;
                    }
                }
            }

            int[] coverage = new int[maxiumLineNumber + 1];
            LineVisitStatus[] lineVisitStatus = new LineVisitStatus[maxiumLineNumber + 1];

            for (int i = 0; i < coverage.Length; i++)
            {
                coverage[i] = -1;
            }

            foreach (var kv in visitsByLine)
            {
                coverage[kv.Key] = kv.Value;

                if (lineVisitStatus[kv.Key] != LineVisitStatus.Covered)
                {
                    bool partiallyCovered = false;

                    ICollection<Branch> branchesOfLine = null;

                    if (branchesByLineNumber.TryGetValue(kv.Key, out branchesOfLine))
                    {
                        partiallyCovered = branchesOfLine.Any(b => b.BranchVisits == 0);
                    }

                    LineVisitStatus statusOfLine = kv.Value > 0 ? (partiallyCovered ? LineVisitStatus.PartiallyCovered : LineVisitStatus.Covered) : LineVisitStatus.NotCovered;
                    lineVisitStatus[kv.Key] = (LineVisitStatus)Math.Max((int)lineVisitStatus[kv.Key], (int)statusOfLine);
                }
            }

            var codeFile = new CodeFile(fileName, coverage, lineVisitStatus, branchesByLineNumber);

            for (int i = 0; i < codeElements.Count; i++)
            {
                var codeElement = codeElements[i];

                int lastLine = maxiumLineNumber;
                if (i < codeElements.Count - 1)
                {
                    lastLine = codeElements[i + 1].FirstLine - 1;
                }

                codeFile.AddCodeElement(new CodeElement(
                    codeElement.Name,
                    CodeElementType.Method,
                    codeElement.FirstLine,
                    lastLine,
                    codeFile.CoverageQuota(codeElement.FirstLine, lastLine)));
            }

            @class.AddFile(codeFile);
        }
    }
}
