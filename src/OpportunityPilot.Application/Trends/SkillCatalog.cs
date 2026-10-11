using System.Text.RegularExpressions;

namespace OpportunityPilot.Application.Trends;

/// <summary>A fixed list of skills and tools looked for in a posting's title and text. Only a listed skill is ever counted.</summary>
public static class SkillCatalog
{
    // "Go" is ambiguous in plain English, so it is not listed; "Golang" is.
    private static readonly string[] Names =
    [
        "C#", ".NET", "ASP.NET", "Java", "Spring Boot", "Kotlin", "Scala", "Python", "Django", "Flask", "FastAPI", "Ruby", "Rails", "PHP", "Laravel",
        "JavaScript", "TypeScript", "Node.js", "React", "Next.js", "Angular", "Vue", "Svelte", "HTML", "CSS", "Tailwind", "GraphQL", "REST",
        "Golang", "Rust", "C++", "Swift", "Objective-C", "Flutter", "React Native", "Android", "iOS",
        "SQL", "PostgreSQL", "MySQL", "SQL Server", "Oracle", "MongoDB", "Redis", "Elasticsearch", "Snowflake", "BigQuery", "Databricks", "Spark", "Kafka", "Airflow", "dbt", "ETL",
        "AWS", "Azure", "GCP", "Docker", "Kubernetes", "Terraform", "Ansible", "Jenkins", "GitHub Actions", "CI/CD", "DevOps", "Linux", "Microservices",
        "Machine Learning", "Deep Learning", "NLP", "LLM", "Generative AI", "PyTorch", "TensorFlow", "Computer Vision", "Data Science", "Data Engineering", "Power BI", "Tableau", "Excel",
        "Salesforce", "SAP", "Dynamics 365", "Power Platform", "ServiceNow", "Workday", "NetSuite", "HubSpot", "Zendesk",
        "Cybersecurity", "SIEM", "Penetration Testing", "IAM",
        "QA", "Selenium", "Cypress", "Playwright", "Test Automation", "Agile", "Scrum", "Jira", "Figma", "UX", "Product Management",
        "Sales", "Business Development", "Account Management", "Customer Success", "Marketing", "SEO", "Recruiting", "Finance", "Accounting", "Compliance",
    ];

    private static readonly (string Name, Regex Pattern)[] Patterns = Names
        .Select(n => (n, new Regex(@"(?<![A-Za-z0-9+#.])" + Regex.Escape(n) + @"(?![A-Za-z0-9+#])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))))
        .ToArray();

    public static IReadOnlyList<string> Find(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<string>();
        foreach (var (name, pattern) in Patterns)
        {
            try { if (pattern.IsMatch(text)) found.Add(name); }
            catch (RegexMatchTimeoutException) { /* a pathological text simply yields no match for this skill */ }
        }
        return found;
    }
}
