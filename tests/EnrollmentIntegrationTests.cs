using System;
using System.Collections.Generic;
using Moq;
using Npgsql;
using Xunit;

namespace Courses.Tests;

[Collection("Database")]
public class EnrollmentIntegrationTests : IDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly StudentRepository studentRepository;
    private readonly CourseRepository courseRepository;
    private readonly EnrollmentRepository enrollmentRepository;
    private readonly Mock<IEnrollmentPolicy> policyMock;
    private readonly EnrollmentService service;

    public EnrollmentIntegrationTests()
    {
        connection = Database.Connect();
        using var truncateCmd = connection.CreateCommand();
        truncateCmd.CommandText = "TRUNCATE enrollments, courses, students RESTART IDENTITY CASCADE";
        truncateCmd.ExecuteNonQuery();

        studentRepository = new StudentRepository(connection);
        courseRepository = new CourseRepository(connection);
        enrollmentRepository = new EnrollmentRepository(connection);
        policyMock = new Mock<IEnrollmentPolicy>(MockBehavior.Strict);
        service = new EnrollmentService(studentRepository, courseRepository, enrollmentRepository, policyMock.Object);
    }

    public void Dispose()
    {
        connection.Dispose();
    }

    [Fact]
    public void CreateAndReadStudent_repositoryState()
    {
        var student = new Student(0, "STD-01", 20, 85, false);
        var created = studentRepository.Create(student);

        Assert.True(created.Id > 0);
        var found = studentRepository.FindById(created.Id);

        Assert.NotNull(found);
        Assert.Equal(created.Id, found.Id);
        Assert.Equal("STD-01", found.Code);
        Assert.Equal(20, found.Age);
        Assert.Equal(85, found.EntryScore);
        Assert.False(found.Regular);
    }

    [Fact]
    public void CreateAndReadCourse_repositoryState()
    {
        var course = new Course(0, "CRS-01", 300000, 25, 2);
        var created = courseRepository.Create(course);

        Assert.True(created.Id > 0);
        var found = courseRepository.FindById(created.Id);

        Assert.NotNull(found);
        Assert.Equal(created.Id, found.Id);
        Assert.Equal("CRS-01", found.Code);
        Assert.Equal(300000, found.Price);
        Assert.Equal(25, found.Capacity);
        Assert.Equal(2, found.Level);
    }

    [Fact]
    public void CreateAndReadEnrollment_repositoryState()
    {
        var student = studentRepository.Create(new Student(0, "STD-02", 22, 90, true));
        var course = courseRepository.Create(new Course(0, "CRS-02", 400000, 30, 1));
        var enrollment = new Enrollment(0, "REF-001", student.Id, course.Id, 400000, 100000, 0, EnrollmentStatus.Enrolled);

        var created = enrollmentRepository.Create(enrollment);
        Assert.True(created.Id > 0);

        var found = enrollmentRepository.FindById(created.Id);
        Assert.NotNull(found);
        Assert.Equal(created.Id, found.Id);
        Assert.Equal("REF-001", found.Reference);
        Assert.Equal(student.Id, found.StudentId);
        Assert.Equal(course.Id, found.CourseId);
        Assert.Equal(400000, found.Total);
        Assert.Equal(100000, found.Prepaid);
        Assert.Equal(0, found.Refund);
        Assert.Equal(EnrollmentStatus.Enrolled, found.Status);
    }

    [Fact]
    public void UpdateStudent_repositoryState()
    {
        var student = studentRepository.Create(new Student(0, "STD-03", 19, 70, false));
        var updated = student with { Code = "STD-03-UPD", Age = 20, EntryScore = 75, Regular = true };

        studentRepository.Update(updated);

        var found = studentRepository.FindById(student.Id);
        Assert.NotNull(found);
        Assert.Equal("STD-03-UPD", found.Code);
        Assert.Equal(20, found.Age);
        Assert.Equal(75, found.EntryScore);
        Assert.True(found.Regular);
    }

    [Fact]
    public void FindEnrollments_FiltersByCourseAndStatus()
    {
        var student1 = studentRepository.Create(new Student(0, "STD-04", 21, 80, false));
        var student2 = studentRepository.Create(new Student(0, "STD-05", 22, 85, false));
        var course1 = courseRepository.Create(new Course(0, "CRS-03", 200000, 10, 1));
        var course2 = courseRepository.Create(new Course(0, "CRS-04", 250000, 10, 1));

        var e1 = enrollmentRepository.Create(new Enrollment(0, "REF-002", student1.Id, course1.Id, 200000, 50000, 0, EnrollmentStatus.Enrolled));
        enrollmentRepository.Create(new Enrollment(0, "REF-003", student2.Id, course1.Id, 200000, 50000, 50000, EnrollmentStatus.Cancelled));
        enrollmentRepository.Create(new Enrollment(0, "REF-004", student1.Id, course2.Id, 250000, 62500, 0, EnrollmentStatus.Enrolled));

        var enrolledInCourse1 = enrollmentRepository.FindEnrollments(course1.Id, EnrollmentStatus.Enrolled);

        Assert.Single(enrolledInCourse1);
        Assert.Equal(e1.Id, enrolledInCourse1[0].Id);
    }

    [Fact]
    public void DeleteEnrollment_RemovesRecord()
    {
        var student = studentRepository.Create(new Student(0, "STD-06", 23, 88, false));
        var course = courseRepository.Create(new Course(0, "CRS-05", 200000, 10, 1));
        var enrollment = enrollmentRepository.Create(new Enrollment(0, "REF-005", student.Id, course.Id, 200000, 50000, 0, EnrollmentStatus.Enrolled));

        enrollmentRepository.Delete(enrollment.Id);

        var found = enrollmentRepository.FindById(enrollment.Id);
        Assert.Null(found);
    }

    [Fact]
    public void Enroll_Successful_serviceState()
    {
        var student = studentRepository.Create(new Student(0, "STD-10", 25, 95, false));
        var course = courseRepository.Create(new Course(0, "CRS-10", 400000, 15, 1));

        policyMock.Setup(p => p.IsAllowed(It.Is<Student>(s => s.Id == student.Id), It.Is<Course>(c => c.Id == course.Id)))
                  .Returns(true);

        var result = service.Enroll(new EnrollmentRequest("REF-100", student.Id, course.Id));

        Assert.True(result.Id > 0);
        Assert.Equal("REF-100", result.Reference);
        Assert.Equal(EnrollmentStatus.Enrolled, result.Status);
        Assert.Equal(400000, result.Total);
        Assert.Equal(100000, result.Prepaid);
        Assert.Equal(0, result.Refund);

        var stored = enrollmentRepository.FindById(result.Id);
        Assert.NotNull(stored);
        Assert.Equal(result.Id, stored.Id);
        Assert.Equal(result.Total, stored.Total);
        Assert.Equal(result.Prepaid, stored.Prepaid);
        Assert.Equal(EnrollmentStatus.Enrolled, stored.Status);

        policyMock.Verify(p => p.IsAllowed(It.IsAny<Student>(), It.IsAny<Course>()), Times.Once);
    }

    [Fact]
    public void Enroll_PolicyDenied_serviceState()
    {
        var student = studentRepository.Create(new Student(0, "STD-11", 24, 75, false));
        var course = courseRepository.Create(new Course(0, "CRS-11", 300000, 10, 1));

        policyMock.Setup(p => p.IsAllowed(It.Is<Student>(s => s.Id == student.Id), It.Is<Course>(c => c.Id == course.Id)))
                  .Returns(false);

        Assert.Throws<EnrollmentRejectedException>(() =>
            service.Enroll(new EnrollmentRequest("REF-101", student.Id, course.Id)));

        Assert.False(enrollmentRepository.HasEnrollment(student.Id, course.Id));
        Assert.Empty(enrollmentRepository.FindEnrollments(course.Id, EnrollmentStatus.Enrolled));

        policyMock.Verify(p => p.IsAllowed(It.IsAny<Student>(), It.IsAny<Course>()), Times.Once);
    }

    [Fact]
    public void Enroll_PolicyException_ThrowsAndLeavesDatabaseEmpty()
    {
        var student = studentRepository.Create(new Student(0, "STD-12", 23, 80, false));
        var course = courseRepository.Create(new Course(0, "CRS-12", 300000, 10, 1));

        policyMock.Setup(p => p.IsAllowed(It.IsAny<Student>(), It.IsAny<Course>()))
                  .Throws(new InvalidOperationException("Помилка зовнішнього сервісу"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.Enroll(new EnrollmentRequest("REF-102", student.Id, course.Id)));

        Assert.Equal("Помилка зовнішнього сервісу", ex.Message);
        Assert.False(enrollmentRepository.HasEnrollment(student.Id, course.Id));
    }

    [Fact]
    public void Enroll_PrepaymentMinimum_AppliesMinimum500Hryvnias_serviceState()
    {
        var student = studentRepository.Create(new Student(0, "STD-13", 20, 80, false));
        var course = courseRepository.Create(new Course(0, "CRS-13", 100000, 10, 1));

        policyMock.Setup(p => p.IsAllowed(It.IsAny<Student>(), It.IsAny<Course>())).Returns(true);

        var result = service.Enroll(new EnrollmentRequest("REF-103", student.Id, course.Id));

        Assert.Equal(100000, result.Total);
        Assert.Equal(50000, result.Prepaid);

        var stored = enrollmentRepository.FindById(result.Id);
        Assert.NotNull(stored);
        Assert.Equal(50000, stored.Prepaid);
    }

    [Fact]
    public void Enroll_PrepaymentCheapCourse_FullPrice()
    {
        var student = studentRepository.Create(new Student(0, "STD-14", 20, 80, false));
        var course = courseRepository.Create(new Course(0, "CRS-14", 30000, 10, 1));

        policyMock.Setup(p => p.IsAllowed(It.IsAny<Student>(), It.IsAny<Course>())).Returns(true);

        var result = service.Enroll(new EnrollmentRequest("REF-104", student.Id, course.Id));

        Assert.Equal(30000, result.Total);
        Assert.Equal(30000, result.Prepaid);

        var stored = enrollmentRepository.FindById(result.Id);
        Assert.NotNull(stored);
        Assert.Equal(30000, stored.Prepaid);
    }

    [Fact]
    public void Enroll_PrepaymentStandard25Percent()
    {
        var student = studentRepository.Create(new Student(0, "STD-15", 20, 80, false));
        var course = courseRepository.Create(new Course(0, "CRS-15", 800000, 10, 1));

        policyMock.Setup(p => p.IsAllowed(It.IsAny<Student>(), It.IsAny<Course>())).Returns(true);

        var result = service.Enroll(new EnrollmentRequest("REF-105", student.Id, course.Id));

        Assert.Equal(800000, result.Total);
        Assert.Equal(200000, result.Prepaid);

        var stored = enrollmentRepository.FindById(result.Id);
        Assert.NotNull(stored);
        Assert.Equal(200000, stored.Prepaid);
    }

    [Fact]
    public void Enroll_CapacityExceeded_RejectsWithoutCallingPolicy()
    {
        var student1 = studentRepository.Create(new Student(0, "STD-16", 20, 80, false));
        var student2 = studentRepository.Create(new Student(0, "STD-17", 21, 85, false));
        var course = courseRepository.Create(new Course(0, "CRS-16", 200000, 1, 1));

        enrollmentRepository.Create(new Enrollment(0, "REF-106", student1.Id, course.Id, 200000, 50000, 0, EnrollmentStatus.Enrolled));

        var ex = Assert.Throws<EnrollmentRejectedException>(() =>
            service.Enroll(new EnrollmentRequest("REF-107", student2.Id, course.Id)));

        Assert.Equal("Вільних місць немає", ex.Message);
        Assert.False(enrollmentRepository.HasEnrollment(student2.Id, course.Id));
        policyMock.Verify(p => p.IsAllowed(It.IsAny<Student>(), It.IsAny<Course>()), Times.Never);
    }

    [Fact]
    public void Enroll_DuplicateEnrollment_RejectsWithoutCallingPolicy()
    {
        var student = studentRepository.Create(new Student(0, "STD-18", 22, 90, false));
        var course = courseRepository.Create(new Course(0, "CRS-17", 200000, 10, 1));

        enrollmentRepository.Create(new Enrollment(0, "REF-108", student.Id, course.Id, 200000, 50000, 0, EnrollmentStatus.Enrolled));

        var ex = Assert.Throws<EnrollmentRejectedException>(() =>
            service.Enroll(new EnrollmentRequest("REF-109", student.Id, course.Id)));

        Assert.Equal("Слухача вже зараховано або він завершив курс", ex.Message);
        policyMock.Verify(p => p.IsAllowed(It.IsAny<Student>(), It.IsAny<Course>()), Times.Never);
    }

    [Fact]
    public void Cancel_PersistsCancelledStatusAndRefund()
    {
        var student = studentRepository.Create(new Student(0, "STD-19", 20, 80, false));
        var course = courseRepository.Create(new Course(0, "CRS-18", 300000, 10, 1));
        var enrollment = enrollmentRepository.Create(new Enrollment(0, "REF-110", student.Id, course.Id, 300000, 75000, 0, EnrollmentStatus.Enrolled));

        var cancelled = service.Cancel(enrollment.Id);

        Assert.Equal(EnrollmentStatus.Cancelled, cancelled.Status);
        Assert.Equal(75000, cancelled.Refund);

        var stored = enrollmentRepository.FindById(enrollment.Id);
        Assert.NotNull(stored);
        Assert.Equal(EnrollmentStatus.Cancelled, stored.Status);
        Assert.Equal(75000, stored.Refund);
        Assert.Equal(75000, stored.Prepaid);
        Assert.Equal(300000, stored.Total);
    }

    [Fact]
    public void Complete_PersistsCompletedStatus()
    {
        var student = studentRepository.Create(new Student(0, "STD-20", 20, 80, false));
        var course = courseRepository.Create(new Course(0, "CRS-19", 300000, 10, 1));
        var enrollment = enrollmentRepository.Create(new Enrollment(0, "REF-111", student.Id, course.Id, 300000, 75000, 0, EnrollmentStatus.Enrolled));

        var completed = service.Complete(enrollment.Id);

        Assert.Equal(EnrollmentStatus.Completed, completed.Status);
        Assert.Equal(0, completed.Refund);

        var stored = enrollmentRepository.FindById(enrollment.Id);
        Assert.NotNull(stored);
        Assert.Equal(EnrollmentStatus.Completed, stored.Status);
        Assert.Equal(0, stored.Refund);
        Assert.Equal(75000, stored.Prepaid);
    }
}
