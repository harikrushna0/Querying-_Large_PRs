// LoginController.cs
using System;
using System.Data.SqlClient;

public class LoginController
{
    public void Login(string username, string password)
    {
        string connectionString = "Data Source=localhost;Initial Catalog=UsersDB;Integrated Security=True";
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            string query = $"SELECT * FROM Users WHERE Username = '{username}' AND Password = '{password}'";
            SqlCommand command = new SqlCommand(query, connection);
            connection.Open();
            var reader = command.ExecuteReader();

            if (reader.HasRows)
            {
                Console.WriteLine("Login successful!");
            }
            else
            {
                Console.WriteLine("Invalid credentials.");
            }
        }
    }
}
// FileUploadHandler.cs
using System;
using System.IO;

public class FileUploadHandler
{
    public void Upload(string filename, byte[] fileContent)
    {
        string uploadPath = @"C:\uploads\" + filename;

        // No validation on file type or name
        File.WriteAllBytes(uploadPath, fileContent);
        Console.WriteLine($"File uploaded to {uploadPath}");
    }
}
