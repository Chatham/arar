Imports System.Threading
Imports System.Configuration
Imports System.Collections.Generic
Imports System.Data.SqlClient
Imports System.Diagnostics
Imports TDAPIOLELib
Imports ServiceStack

Module ARAR

    Dim Sync As New Object

    Enum Availability
        Available
        NotAvailable
    End Enum

    Class Machine
        Public Name As String
        Public Status As Availability
        Public conn = New SqlConnection(ConfigurationManager.ConnectionStrings("dbConnection").ConnectionString.ToString())
        Public sqlCall As SqlCommand
        Public sqlCallGenericReader
        Public sqlCallIntReader As Integer
        Public sqlCallStringReader As String
        Public strHostMachine As String
        Public strTestCycle As String
        Public strTestName As String
        Public strPlannedHost As String
        Public strSqlStatement As String

        Public Sub New(ByVal name As String, ByVal status As Availability)
            Me.Name = name
            Me.Status = status
        End Sub

        Public Sub Start()
            Debug.Print("starting thread for machine {0}", Name)
            UpdateMyAvailability()
            Debug.Print("status for machine {0} is {1}", Name, Status)
            While (IAmAvailable())
                Debug.Print("machine {0} is available, kicking off test", Name)
                RunNext()
                UpdateMyAvailability()
            End While
        End Sub

        Private Function IAmAvailable() As Boolean
            Return Status = Availability.Available
        End Function

        Private Sub UpdateMyAvailability()
            'See if machine is available
            Dim intFailCount As Integer
            Dim runExecDate As String
            Dim runStatus As String
            Dim runDuration As Integer
            Dim maxStepTime As String

            Machines.ForEach(Sub(machine)
                                 If Not IAmAvailable() Then
                                     Try
                                         strSqlStatement = String.Format("SELECT top 1 td.run.RN_STATUS, td.run.RN_DURATION, max(st_execution_time) AS maxStepTime, CONVERT(VARCHAR(10), td.run.RN_EXECUTION_DATE, 101) AS ExecutionDate, max(st_execution_time) AS maxStepTime FROM td.run, td.step where rn_run_id = ST_run_id AND RN_HOST = '{0}' Group By td.run.RN_EXECUTION_DATE, td.run.RN_STATUS, td.run.RN_DURATION, td.run.RN_RUN_ID order by RN_RUN_ID DESC", Me.Name)

                                         conn.Open()
                                         sqlCall = New SqlCommand(strSqlStatement, conn)
                                         sqlCallGenericReader = sqlCall.ExecuteReader()
                                         sqlCallGenericReader.Read()
                                         runStatus = sqlCallGenericReader.GetString(0)
                                         runDuration = sqlCallGenericReader.GetInt32(1)
                                         maxStepTime = sqlCallGenericReader.GetString(2)
                                         runExecDate = sqlCallGenericReader.GetString(3)
                                         conn.Close()

                                         Debug.Print("{0}:  Latest status on machine {1} is {2}", CStr(Now), Me.Name, runStatus)


                                         Select Case runStatus
                                             Case "Passed"
                                                 Debug.Print("  Send the test")
                                                 Me.Status = Availability.Available
                                             Case "Failed"
                                                 If runDuration > 200 Then
                                                     Debug.Print("  Last test on {0} ran {1} seconds, so it's done, send the test", Me.Name, runDuration)
                                                     Me.Status = Availability.Available
                                                 Else
                                                     strSqlStatement = String.Format("SELECT COUNT(*) FROM td.RUN WHERE RN_STATUS = 'Failed' AND RN_RUN_ID IN (SELECT TOP 5 RN_RUN_ID FROM td.RUN WHERE RN_HOST IN ('{0}') ORDER BY RN_RUN_ID DESC)", Me.Name)
                                                     conn.Open()
                                                     Debug.Print("Checking to see if {0} is crashed!", Me.Name)
                                                     sqlCall = New SqlCommand(strSqlStatement, conn)
                                                     sqlCallIntReader = sqlCall.ExecuteScalar
                                                     intFailCount = Convert.ToInt32(sqlCallIntReader)
                                                     conn.Close()

                                                     If intFailCount > 4 Then
                                                         Console.WriteLine("{0}: {1} has 5 of last 5 tests failed, so rebooting", CStr(Now), Me.Name)
                                                         RebootMachine(Me.Name)
                                                         Me.Status = Availability.Available
                                                     Else
                                                         Debug.Print("  Fail count on {0} is only {1}, send the test", Me.Name, intFailCount)
                                                         Me.Status = Availability.Available
                                                     End If
                                                 End If
                                             Case Else
                                                 Dim lastRun As DateTime = String.Format("{0} {1}", runExecDate, maxStepTime)

                                                 If Now.AddSeconds(-90) > lastRun Then
                                                     Debug.Print("  and the last step on {0} ran more than 90 second ago, send the test", Me.Name)
                                                     Me.Status = Availability.Available
                                                 Else
                                                     Debug.Print("  and last step on {0} was less than 90 seconds ago, so it's running a test", Me.Name)
                                                     Me.Status = Availability.NotAvailable
                                                 End If
                                         End Select
                                     Catch ex As Exception
                                         Console.WriteLine("  {0}: Exception {1} occurred. {2} has some serious issues, it's not available", CStr(Now), ex.Message, Me.Name)
                                         RebootMachine(Me.Name)
                                         Me.Status = Availability.Available
                                     End Try
                                 End If
                             End Sub)

        End Sub

        Private Sub RunNext()

            Dim strTestQueue As Integer
            Dim blnFoundIt As Boolean = False
            Dim strMaxQueue As String
            Dim strTestID As String = ""
            Dim strSqlStatement As String

            SyncLock Sync
                strSqlStatement = String.Format("SELECT TOP 1 q_id, q_test_id, q_test_name, q_host FROM {0}", ConfigurationManager.AppSettings("localQueueTable").ToString())
                'Get information on the next test that needs to be rerun - this code is controlled
                conn.Open()
                Debug.Print("Connected to get the first test to be rerun!")
                sqlCall = New SqlCommand(strSqlStatement, conn)
                sqlCallGenericReader = sqlCall.ExecuteReader()
                sqlCallGenericReader.Read()

                If sqlCallGenericReader.HasRows Then
                    strTestQueue = sqlCallGenericReader.GetInt32(0)
                    strTestCycle = sqlCallGenericReader.GetInt32(1)
                    strTestName = sqlCallGenericReader.GetString(2)
                    strPlannedHost = sqlCallGenericReader.GetString(3)
                    conn.Close()

                    'If the planned is not part of the group or the host machine is not available move the test to the end of the queue
                    If strPlannedHost.ToUpper().Contains("QA") = True Then
                        'Check if this machine is part of the host group for the test
                        strSqlStatement = String.Format("SELECT * FROM td.HOSTS WHERE HO_NAME = '{0}' AND HO_ID IN (SELECT HG_HOST_ID FROM td.HOST_IN_GROUP WHERE HG_GROUP_ID IN (SELECT GH_ID FROM td.HOST_GROUP WHERE GH_NAME = '{1}'))", Me.Name, strPlannedHost.ToUpper())
                        conn.Open()
                        Debug.Print("Connected to see if available machine is in host group!")
                        sqlCall = New SqlCommand(strSqlStatement, conn)
                        sqlCallGenericReader = sqlCall.ExecuteReader()

                        If sqlCallGenericReader.HasRows Then
                            blnFoundIt = True
                        End If

                        conn.Close()
                    Else
                        If strPlannedHost.ToUpper().Contains(Me.Name) = True Then
                            blnFoundIt = True
                        Else
                            If strPlannedHost = "" Then
                                'Update the planned host assuming QA since it wasn't specified
                                strSqlStatement = String.Format("UPDATE {0} SET q_host = 'QA' WHERE q_test_id = {1}", ConfigurationManager.AppSettings("localQueueTable").ToString(), strTestCycle)
                                conn.Open()
                                Console.WriteLine("No host specified, so assuming QA")
                                sqlCall = New SqlCommand(strSqlStatement, conn)
                                sqlCallGenericReader = sqlCall.ExecuteReader()
                                sqlCallGenericReader.Read()
                                conn.Close()
                            End If
                        End If
                    End If

                    If blnFoundIt = False Then
                        Debug.Print("No machines available for this test, moving {0} to the end of the queue", strTestCycle)
                        strSqlStatement = String.Format("SELECT MAX(q_id) FROM {0}", ConfigurationManager.AppSettings("localQueueTable").ToString())
                        'Move this test to the end of the queue so others can run (only if a specific machine is requested)
                        conn.Open()
                        sqlCall = New SqlCommand(strSqlStatement, conn)
                        sqlCallIntReader = sqlCall.ExecuteScalar
                        strMaxQueue = Convert.ToString(sqlCallIntReader)
                        conn.Close()

                        strSqlStatement = String.Format("UPDATE {0} SET q_id = {1} WHERE(q_id = {2})", ConfigurationManager.AppSettings("localQueueTable").ToString(), strMaxQueue + 1, strTestQueue)
                        conn.Open()
                        sqlCall = New SqlCommand(strSqlStatement, conn)
                        sqlCallGenericReader = sqlCall.ExecuteReader()
                        conn.Close()
                    Else
                        strTestID = strTestCycle

                        'Remove the row from the table so we don't pick the test up again
                        strSqlStatement = String.Format("DELETE FROM {0} WHERE q_test_id = {1}", ConfigurationManager.AppSettings("localQueueTable").ToString(), strTestID)
                        conn.Open()
                        Debug.Print("Delete the row from the queue table so we don't grab this test again!")
                        sqlCall = New SqlCommand(strSqlStatement, conn)
                        sqlCallGenericReader = sqlCall.ExecuteReader()
                        sqlCallGenericReader.Read()
                        conn.Close()
                    End If
                Else
                    conn.Close()
                End If

            End SyncLock

            ' check if a test should be run
            If (String.IsNullOrWhiteSpace(strTestID)) Then
                ' I dont have a test to run, just wait a minute
                Thread.Sleep(60000)

                Return
            End If

            Try
                ' create an event to wait on
                Dim DoneEvent As New ManualResetEventSlim()

                ' run that shit
                runQCTest(Me.Name, strTestID, DoneEvent, strTestName, strPlannedHost)

                ' wait for the test to complete
                DoneEvent.Wait()
            Catch ex As Exception
                moveToBottom(Me.Name, strTestID, strTestName, strPlannedHost)
                Console.WriteLine("  {0}: Exception {1} occurred in the runQCTest section of RunNext sub", CStr(Now), ex.Message)
                Exit Sub
            End Try

        End Sub

    End Class

    Dim Machines As New List(Of Machine)

    Sub Main()

        InitWww()

        Using machineConn As New SqlConnection(ConfigurationManager.ConnectionStrings("dbConnection").ConnectionString.ToString())
            Dim strSqlStatement As String = "SELECT HO_NAME FROM td.HOSTS WHERE HO_ID IN (SELECT HG_HOST_ID FROM td.HOST_IN_GROUP WHERE HG_GROUP_ID IN (SELECT GH_ID FROM td.HOST_GROUP WHERE GH_NAME = 'MasterQA'))"
            Dim sqlcall As New SqlCommand(strSqlStatement, machineConn)

            machineConn.Open()

            Using sqlCallGenericReader As SqlDataReader = sqlcall.ExecuteReader()
                While sqlCallGenericReader.Read()
                    Machines.Add(New Machine(sqlCallGenericReader("HO_NAME"), Availability.Available))
                End While
            End Using

            machineConn.Close()

        End Using

        Machines.ForEach(Sub(machine)
                             Dim thread As New Thread(AddressOf machine.Start)
                             thread.Start()
                         End Sub)

    End Sub
    Public Sub moveToBottom(ByVal machineName As String, ByVal testID As String, ByVal testName As String, ByVal plannedHost As String)
        Dim strMaxQueue As String

        'Move this test to the end of the queue so others can run (only if a specific machine is requested)
        SyncLock Sync
            If CountQueueRows() = 0 Then
                strMaxQueue = 0
            Else
                strMaxQueue = GetMaxQueueID().ToString
            End If

            InsertQueueRow(strMaxQueue, testID, plannedHost, testName)
        End SyncLock

    End Sub

    Public Function CountQueueRows()
        Dim conn = New SqlConnection(ConfigurationManager.ConnectionStrings("dbConnection").ConnectionString.ToString())
        Dim strSqlStatement As String
        Dim sqlCall As SqlCommand
        Dim sqlCallGenericReader
        Dim intCounter As Integer

        Try
            conn.Open()
            strSqlStatement = String.Format("SELECT COUNT(*) FROM {0}", ConfigurationManager.AppSettings("localQueueTable").ToString())
            sqlCall = New SqlCommand(strSqlStatement, conn)
            sqlCallGenericReader = sqlCall.ExecuteScalar
            intCounter = Convert.ToInt32(sqlCallGenericReader)
            conn.Close()
        Catch ex As Exception
            Console.WriteLine("  {0}: Exception {1} occurred in CountQueueRows", CStr(Now), ex.Message)
            intCounter = 0
        End Try

        Return intCounter

    End Function

    Public Function GetMaxQueueID()
        Dim conn = New SqlConnection(ConfigurationManager.ConnectionStrings("dbConnection").ConnectionString.ToString())
        Dim strSqlStatement As String
        Dim sqlCall As SqlCommand
        Dim sqlCallStringReader As String
        Dim intMaxQueue As Integer

        Try
            strSqlStatement = String.Format("SELECT MAX(q_id) FROM {0}", ConfigurationManager.AppSettings("localQueueTable").ToString())
            conn.Open()
            sqlCall = New SqlCommand(strSqlStatement, conn)
            sqlCallStringReader = sqlCall.ExecuteScalar
            intMaxQueue = Convert.ToInt32(sqlCallStringReader)
            conn.Close()
        Catch ex As Exception
            Console.WriteLine("  {0}: Exception {1} occurred in GetMaxQueueID", CStr(Now), ex.Message)
            intMaxQueue = 0
        End Try

        Return intMaxQueue

    End Function

    Public Sub InsertQueueRow(ByVal strMaxQueue, ByVal testID, ByVal plannedHost, ByVal testName)
        Dim conn = New SqlConnection(ConfigurationManager.ConnectionStrings("dbConnection").ConnectionString.ToString())
        Dim strSqlStatement As String = "Blank"
        Dim sqlCall As SqlCommand
        Dim sqlCallGenericReader

        Try
            strSqlStatement = String.Format("INSERT INTO {0} VALUES ({1}, {2}, '{3}', '{4}', 1)", ConfigurationManager.AppSettings("localQueueTable").ToString(), strMaxQueue + 1, testID, plannedHost, testName)
            conn.Open()
            sqlCall = New SqlCommand(strSqlStatement, conn)
            sqlCallGenericReader = sqlCall.ExecuteReader()
            conn.Close()
        Catch ex As Exception
            Console.WriteLine("  {0}: Exception {1} occurred with the sql {2} in InsertQueueRow", CStr(Now), ex.Message, strSqlStatement)
        End Try
    End Sub
    Public Sub RebootMachine(ByVal comp)

        Using RebootProc As New Process
            With RebootProc.StartInfo
                .CreateNoWindow = True
                .FileName = "shutdown.exe"
                .Arguments = String.Format(" -r -m \\{0} -t 0 -f", comp)
                .UseShellExecute = False
                .WindowStyle = ProcessWindowStyle.Hidden
                .RedirectStandardOutput = True
                .RedirectStandardInput = True
            End With

            RebootProc.Start()
        End Using

        Console.WriteLine("  {0}: Rebooting {1}", CStr(Now), comp)

        Thread.Sleep(10000)

        Do While My.Computer.Network.Ping(comp) = False
            Thread.Sleep(5000)
        Loop

        Console.WriteLine("  {0}: Machine {1} is back up", CStr(Now), comp)

    End Sub
    Public Sub disconnectQC(ByRef qcConnection)
        If qcConnection IsNot Nothing Then
            'Disconnect from the project
            If qcConnection.Connected Then
                qcConnection.Disconnect()
            End If
            'Log off the server
            If qcConnection.LoggedIn Then
                qcConnection.Logout()
            End If
            'Release the TDConnection object.
            qcConnection.ReleaseConnection()
            qcConnection = Nothing
        End If
    End Sub
    Public Sub runQCTest(ByVal machineName As String, ByVal testID As String, ByRef done As ManualResetEventSlim, ByVal testName As String, ByVal plannedHost As String)
        Dim runConn = New SqlConnection(ConfigurationManager.ConnectionStrings("dbConnection").ConnectionString.ToString())
        Dim tdc As Object = Nothing
        Dim strTestSet As String
        Dim strFolder As String
        Dim strSqlStatement As String
        Dim sqlCall As SqlCommand
        Dim sqlCallStringReader As String
        Dim tsTreeMgr As TestSetTreeManager

        'Create connection to Quality Center
        Try
            tdc = CreateObject("tdapiole80.tdconnection")
            tdc.InitConnectionEx(ConfigurationManager.AppSettings("qcHost").ToString)
            tdc.Login(ConfigurationManager.AppSettings("qcUser").ToString, ConfigurationManager.AppSettings("qcPassword").ToString)
            tdc.Connect(ConfigurationManager.AppSettings("qcDomain").ToString, ConfigurationManager.AppSettings("qcProject").ToString)
        Catch ex As Exception
            disconnectQC(tdc)
            'Err.Raise(vbObjectError + 1, "Cannot connect to Quality Center")
            done.Set()
            moveToBottom(machineName, testID, testName, plannedHost)
            Console.WriteLine("  {0}: Exception {1} occurred when connection to QC, moving test {2} to bottom to get picked up with next try", CStr(Now), ex.Message, testID)
            Exit Sub
        End Try

        'Test Folder and Test Set Info
        Dim tsList As List
        Dim theTestSet As TestSet
        Dim tsFolder As TestSetFolder
        Dim Scheduler As TSScheduler
        Dim execStatus As ExecutionStatus

        'Get the folder name for the test we are rerunning
        strSqlStatement = String.Format("SELECT CF_ITEM_NAME FROM td.CYCL_FOLD WHERE CF_ITEM_ID IN (SELECT CY_FOLDER_ID FROM td.CYCLE WHERE CY_CYCLE_ID IN (SELECT RN_CYCLE_ID FROM td.RUN WHERE RN_TESTCYCL_ID = '{0}'))", testID)
        runConn.Open()
        sqlCall = New SqlCommand(strSqlStatement, runConn)
        sqlCallStringReader = sqlCall.ExecuteScalar
        strFolder = Convert.ToString(sqlCallStringReader)
        runConn.Close()

        'Get the test set name for the test we are rerunning
        strSqlStatement = String.Format("SELECT CY_CYCLE FROM td.CYCLE WHERE CY_CYCLE_ID IN (SELECT RN_CYCLE_ID FROM td.RUN WHERE RN_TESTCYCL_ID = '{0}')", testID)
        runConn.Open()
        sqlCall = New SqlCommand(strSqlStatement, runConn)
        sqlCallStringReader = sqlCall.ExecuteScalar
        strTestSet = Convert.ToString(sqlCallStringReader)
        runConn.Close()

        ' Get the test set tree manager from the test set factory then verify the folder exists
        strFolder = "Root\" & strFolder

        Debug.Print("1. Looking for the test folder")

        Try
            tsTreeMgr = tdc.TestSetTreeManager
            tsFolder = tsTreeMgr.NodeByPath(strFolder)
        Catch ex As Exception
            disconnectQC(tdc)
            'Err.Raise(vbObjectError + 1, "RunTestSet", "Could not find folder " & strFolder)
            done.Set()
            moveToBottom(machineName, testID, testName, plannedHost)
            Console.WriteLine("  {0}: Exception {1} occurred when looking for test folder, moving test {2} to bottom to get picked up with next try", CStr(Now), ex.Message, testID)
            Exit Sub
        End Try

        Debug.Print("2. Found the folder now looking for the test set")

        'Now look for the test set
        Try
            tsList = tsFolder.FindTestSets(strTestSet, False)
        Catch ex As Exception
            disconnectQC(tdc)
            'Err.Raise(vbObjectError + 1, , "RunTestSet", "Could not find the test set " & strTestSet)
            done.Set()
            moveToBottom(machineName, testID, testName, plannedHost)
            Console.WriteLine("  {0}: Exception {1} occurred when looking for test set {2} for machine {3}, moving test {4} to bottom to get picked up with next try", CStr(Now), ex.Message, strTestSet, machineName, testName)
            Exit Sub
        End Try

        'If tsList.Count > 1 Then
        'Err.Raise(vbObjectError + 1, , "RunTestSet", "FindTestSets found more than one test, please refine search")
        'ElseIf tsList.Count < 1 Then
        'Err.Raise(vbObjectError + 1, "RunTestSet", "FindTestSets " & strTestSet & " test set not found")
        'End If

        theTestSet = tsList.Item(1)
        Debug.Print("3. Found the test set it's time to send the test")
        Console.WriteLine("{0}: Running test {1} on machine {2}", CStr(Now), testID, machineName)

        Try
            ' Start the scheduler on the local machine.
            Scheduler = theTestSet.StartExecution(machineName)
            ' Run tests on a specified remote machine.
            Scheduler.TdHostName = machineName
            ' Run the tests.
            Scheduler.Run(testID)
        Catch ex As Exception
            disconnectQC(tdc)
            done.Set()
            moveToBottom(machineName, testID, testName, plannedHost)
            Console.WriteLine("  {0}: Exception {1} occurred during test execution for machine {2}, so moving test {3} to bottom to get picked up with next try", CStr(Now), ex.Message, machineName, testID)
            Exit Sub
        End Try
        ' Get the execution status object.
        execStatus = Scheduler.ExecutionStatus

        'Track the events and statuses.
        Dim RunFinished As Boolean, iter As Integer, i As Integer
        Dim ExecEventInfoObj As ExecEventInfo
        Dim EventsList As List
        Dim TestExecStatusObj As TestExecStatus
        Dim strType As String = ""

        iter = 0
        RunFinished = False

        Debug.Print("****Test Execution Status****")
        While ((RunFinished = False) And (iter < 200))
            iter = iter + 1
            execStatus.RefreshExecStatusInfo("all", True)
            RunFinished = execStatus.Finished
            EventsList = execStatus.EventsList

            For Each ExecEventInfoObj In EventsList
                Select Case ExecEventInfoObj.EventType
                    Case "1"
                        strType = "Fail"
                    Case "2"
                        strType = "Finished"
                    Case "3"
                        strType = "Environment Failure"
                    Case "4"
                        strType = "Timeout"
                    Case "5"
                        strType = "Manual"
                End Select

                Console.WriteLine("****Scheduler finished test {0} on machine {1} around {2} with a status of {3}", testID, machineName, CStr(Now), strType)
            Next

            For i = 1 To execStatus.Count
                TestExecStatusObj = execStatus.Item(i)
                Debug.Print("Iteration: " & iter & ", Test ID: " & TestExecStatusObj.TestId & ", Test Cycle ID: " & TestExecStatusObj.TSTestId & ", Status: " & TestExecStatusObj.Status & ", Machine: " & machineName)
                Debug.Print(" Message: " & TestExecStatusObj.Message)
            Next i
            Threading.Thread.Sleep(10000)
        End While 'Loop While execStatus.Finished = False

        'Disconnect from the project
        disconnectQC(tdc)
        ' IM ALL DONE, SO I NEED TO LET ME CALLER KNOW
        done.Set()
    End Sub

    Public Sub InitWww()
        Dim listeningOn = ConfigurationManager.AppSettings("listenerURL").ToString
        Dim appHost As New AppHost()

        Console.WriteLine("Listening URL is currently set on {0}", listeningOn)
        appHost.Init()
        appHost.Start(listeningOn)

        Console.WriteLine("AppHost Created at {0}, listening on {1}", DateTime.Now, listeningOn)
    End Sub

    <ServiceStack.ServiceHost.Route("/arar/system/{command}", "GET")>
    Public Class ArarCmdRequest
        Property Command As String
    End Class

    <ServiceStack.ServiceHost.Route("/arar/machine/{machineId}/{command}", "POST")>
    Public Class MachineCmdRequest
        Property MachineId As String
        Property Command As String
    End Class

    Public Class AppHost
        Inherits ServiceStack.WebHost.Endpoints.AppHostHttpListenerBase
        Public Sub New()
            MyBase.New("ARAR HttpListener", GetType(ARAR.ArarHttp).Assembly)
        End Sub

        Public Overrides Sub Configure(ByVal container As Funq.Container)

        End Sub
    End Class

    Public Class ArarHttp
        Implements ServiceHost.IService

        Public Function [Get](ByVal request As ArarCmdRequest) As Object
            Select Case request.Command.ToUpper
                Case "START"
                    Return "Starting"
                Case "STOP"
                    Return "Stopping"
            End Select

            Return "Invalid command: " + request.Command

        End Function

        Public Function [Post](ByVal request As MachineCmdRequest) As Object
            RebootMachine(request.MachineId)
            Return "Machine " + request.MachineId + " is being asked to: " + request.Command
        End Function

    End Class

End Module
