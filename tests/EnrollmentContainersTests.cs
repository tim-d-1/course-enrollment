using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Courses.Tests;

public class EnrollmentContainersFixture : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public IContainer Wiremock { get; } = new ContainerBuilder("wiremock/wiremock:3.13.1")
        .WithPortBinding(8080, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/__admin/mappings")))
        .Build();

    public string WiremockBaseUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Postgres.StartAsync();
        await Wiremock.StartAsync();

        WiremockBaseUrl = $"http://{Wiremock.Hostname}:{Wiremock.GetMappedPublicPort(8080)}";

        await using var connection = new NpgsqlConnection(Postgres.GetConnectionString());
        await connection.OpenAsync();

        var migrationFile = FindMigrationFile();
        var sql = await File.ReadAllTextAsync(migrationFile);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await Wiremock.DisposeAsync();
        await Postgres.DisposeAsync();
    }

    private static string FindMigrationFile()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "migrations", "V1__create_tables.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            var parent = Directory.GetParent(dir);
            if (parent == null)
            {
                break;
            }
            dir = parent.FullName;
        }
        throw new FileNotFoundException("Migration file not found");
    }
}

public class EnrollmentContainersTests : IClassFixture<EnrollmentContainersFixture>, IAsyncLifetime
{
    private readonly EnrollmentContainersFixture fixture;
    private readonly HttpClient httpClient = new HttpClient();
    private NpgsqlConnection connection = null!;
    private StudentRepository studentRepository = null!;
    private CourseRepository courseRepository = null!;
    private EnrollmentRepository enrollmentRepository = null!;
    private HttpEnrollmentPolicy policy = null!;
    private EnrollmentService service = null!;

    public EnrollmentContainersTests(EnrollmentContainersFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        connection = new NpgsqlConnection(fixture.Postgres.GetConnectionString());
        await connection.OpenAsync();

        studentRepository = new StudentRepository(connection);
        courseRepository = new CourseRepository(connection);
        enrollmentRepository = new EnrollmentRepository(connection);
        policy = new HttpEnrollmentPolicy(new Uri(fixture.WiremockBaseUrl), TimeSpan.FromSeconds(5));
        service = new EnrollmentService(studentRepository, courseRepository, enrollmentRepository, policy);

        await ResetStateAsync();
    }

    public async Task DisposeAsync()
    {
        policy.Dispose();
        httpClient.Dispose();
        if (connection != null)
        {
            await connection.DisposeAsync();
        }
    }

    private async Task ResetStateAsync()
    {
        using var resetRequest = new HttpRequestMessage(HttpMethod.Post, $"{fixture.WiremockBaseUrl}/__admin/reset")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        var resetResponse = await httpClient.SendAsync(resetRequest);
        resetResponse.EnsureSuccessStatusCode();

        await using var truncateCmd = connection.CreateCommand();
        truncateCmd.CommandText = "TRUNCATE enrollments, courses, students RESTART IDENTITY CASCADE";
        await truncateCmd.ExecuteNonQueryAsync();
    }

    private async Task StubPolicyAsync(int statusCode, string responseBody)
    {
        var stubPayload = new
        {
            request = new
            {
                method = "POST",
                url = "/enrollment-policy"
            },
            response = new
            {
                status = statusCode,
                headers = new
                {
                    Content_Type = "application/json"
                },
                body = responseBody
            }
        };

        var json = JsonSerializer.Serialize(stubPayload).Replace("Content_Type", "Content-Type");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.WiremockBaseUrl}/__admin/mappings")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task VerifyWiremockRequestsAsync(int expectedCount, long? expectedStudentId = null, long? expectedCourseId = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            method = "POST",
            url = "/enrollment-policy"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.WiremockBaseUrl}/__admin/requests/find")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var requestsArray = doc.RootElement.GetProperty("requests");

        Assert.Equal(expectedCount, requestsArray.GetArrayLength());

        if (expectedCount > 0 && expectedStudentId.HasValue && expectedCourseId.HasValue)
        {
            var firstReq = requestsArray[0];
            var requestBodyString = firstReq.GetProperty("body").GetString();
            using var reqDoc = JsonDocument.Parse(requestBodyString!);
            Assert.Equal(expectedStudentId.Value, reqDoc.RootElement.GetProperty("studentId").GetInt64());
            Assert.Equal(expectedCourseId.Value, reqDoc.RootElement.GetProperty("courseId").GetInt64());
        }
    }

    [Fact]
    public async Task Accepted_WireMockAllowed_CreatesPersistedRecordAndSendsHttpRequest()
    {
        await StubPolicyAsync(200, "{\"allowed\":true}");

        var student = studentRepository.Create(new Student(0, "ST-TC-01", 22, 90, false));
        var course = courseRepository.Create(new Course(0, "CR-TC-01", 800000, 10, 1));

        var enrollment = service.Enroll(new EnrollmentRequest("REF-TC-01", student.Id, course.Id));

        Assert.True(enrollment.Id > 0);
        Assert.Equal(800000, enrollment.Total);
        Assert.Equal(200000, enrollment.Prepaid);
        Assert.Equal(EnrollmentStatus.Enrolled, enrollment.Status);

        var stored = enrollmentRepository.FindById(enrollment.Id);
        Assert.NotNull(stored);
        Assert.Equal(enrollment.Id, stored.Id);
        Assert.Equal(800000, stored.Total);
        Assert.Equal(200000, stored.Prepaid);
        Assert.Equal(0, stored.Refund);
        Assert.Equal(EnrollmentStatus.Enrolled, stored.Status);

        await VerifyWiremockRequestsAsync(1, student.Id, course.Id);
    }

    [Fact]
    public async Task Denied_WireMockNotAllowed_ThrowsEnrollmentRejectedAndLeavesDatabaseEmpty()
    {
        await StubPolicyAsync(200, "{\"allowed\":false}");

        var student = studentRepository.Create(new Student(0, "ST-TC-02", 21, 80, false));
        var course = courseRepository.Create(new Course(0, "CR-TC-02", 400000, 10, 1));

        var ex = Assert.Throws<EnrollmentRejectedException>(() =>
            service.Enroll(new EnrollmentRequest("REF-TC-02", student.Id, course.Id)));

        Assert.Equal("Зарахування не дозволено", ex.Message);
        Assert.False(enrollmentRepository.HasEnrollment(student.Id, course.Id));
        Assert.Equal(0, enrollmentRepository.CountOccupied(course.Id));

        await VerifyWiremockRequestsAsync(1, student.Id, course.Id);
    }

    [Fact]
    public async Task Unavailable_WireMock503_ThrowsPolicyExceptionAndLeavesDatabaseEmpty()
    {
        await StubPolicyAsync(503, "{\"error\":\"Service Unavailable\"}");

        var student = studentRepository.Create(new Student(0, "ST-TC-03", 23, 85, false));
        var course = courseRepository.Create(new Course(0, "CR-TC-03", 500000, 10, 1));

        Assert.Throws<EnrollmentPolicyException>(() =>
            service.Enroll(new EnrollmentRequest("REF-TC-03", student.Id, course.Id)));

        Assert.False(enrollmentRepository.HasEnrollment(student.Id, course.Id));
        Assert.Equal(0, enrollmentRepository.CountOccupied(course.Id));

        await VerifyWiremockRequestsAsync(1, student.Id, course.Id);
    }

    [Fact]
    public async Task LocalRejection_NoSeatsAvailable_DoesNotCallWireMockAndLeavesDatabaseUnchanged()
    {
        await StubPolicyAsync(200, "{\"allowed\":true}");

        var student1 = studentRepository.Create(new Student(0, "ST-TC-04", 20, 80, false));
        var student2 = studentRepository.Create(new Student(0, "ST-TC-05", 21, 82, false));
        var course = courseRepository.Create(new Course(0, "CR-TC-04", 300000, 1, 1));

        enrollmentRepository.Create(new Enrollment(0, "REF-TC-04", student1.Id, course.Id, 300000, 75000, 0, EnrollmentStatus.Enrolled));

        var ex = Assert.Throws<EnrollmentRejectedException>(() =>
            service.Enroll(new EnrollmentRequest("REF-TC-05", student2.Id, course.Id)));

        Assert.Equal("Вільних місць немає", ex.Message);
        Assert.False(enrollmentRepository.HasEnrollment(student2.Id, course.Id));
        Assert.Equal(1, enrollmentRepository.CountOccupied(course.Id));

        await VerifyWiremockRequestsAsync(0);
    }

    [Fact]
    public async Task LocalRejection_AlreadyEnrolled_DoesNotCallWireMock()
    {
        await StubPolicyAsync(200, "{\"allowed\":true}");

        var student = studentRepository.Create(new Student(0, "ST-TC-06", 24, 88, false));
        var course = courseRepository.Create(new Course(0, "CR-TC-05", 300000, 5, 1));

        enrollmentRepository.Create(new Enrollment(0, "REF-TC-06", student.Id, course.Id, 300000, 75000, 0, EnrollmentStatus.Enrolled));

        var ex = Assert.Throws<EnrollmentRejectedException>(() =>
            service.Enroll(new EnrollmentRequest("REF-TC-07", student.Id, course.Id)));

        Assert.Equal("Слухача вже зараховано або він завершив курс", ex.Message);

        await VerifyWiremockRequestsAsync(0);
    }

    [Fact]
    public async Task PersonalRule_PrepaymentMinimum500Hryvnias_PersistedInDatabase()
    {
        await StubPolicyAsync(200, "{\"allowed\":true}");

        var student = studentRepository.Create(new Student(0, "ST-TC-07", 20, 75, false));
        var course = courseRepository.Create(new Course(0, "CR-TC-06", 120000, 10, 1));

        var enrollment = service.Enroll(new EnrollmentRequest("REF-TC-08", student.Id, course.Id));

        Assert.Equal(120000, enrollment.Total);
        Assert.Equal(50000, enrollment.Prepaid);

        var stored = enrollmentRepository.FindById(enrollment.Id);
        Assert.NotNull(stored);
        Assert.Equal(50000, stored.Prepaid);

        await VerifyWiremockRequestsAsync(1, student.Id, course.Id);
    }
}
