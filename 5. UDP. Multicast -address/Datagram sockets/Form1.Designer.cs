
namespace Datagram_sockets
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            button1 = new System.Windows.Forms.Button();
            listBoxMessages = new System.Windows.Forms.ListBox();
            textBoxMessage = new System.Windows.Forms.TextBox();
            listBoxUsers = new System.Windows.Forms.ListBox();
            labelUsers = new System.Windows.Forms.Label();
            labelUserName = new System.Windows.Forms.Label();
            SuspendLayout();
            // 
            // button1
            // 
            button1.Location = new System.Drawing.Point(12, 380);
            button1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            button1.Name = "button1";
            button1.Size = new System.Drawing.Size(200, 35);
            button1.TabIndex = 0;
            button1.Text = "Send Message";
            button1.UseVisualStyleBackColor = true;
            button1.Click += button1_Click;
            // 
            // listBoxMessages
            // 
            listBoxMessages.FormattingEnabled = true;
            listBoxMessages.ItemHeight = 20;
            listBoxMessages.Location = new System.Drawing.Point(12, 45);
            listBoxMessages.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            listBoxMessages.Name = "listBoxMessages";
            listBoxMessages.Size = new System.Drawing.Size(600, 324);
            listBoxMessages.TabIndex = 2;
            // 
            // textBoxMessage
            // 
            textBoxMessage.Location = new System.Drawing.Point(12, 425);
            textBoxMessage.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            textBoxMessage.Multiline = true;
            textBoxMessage.Name = "textBoxMessage";
            textBoxMessage.Size = new System.Drawing.Size(600, 60);
            textBoxMessage.TabIndex = 3;
            textBoxMessage.KeyDown += textBoxMessage_KeyDown;
            // 
            // listBoxUsers
            // 
            listBoxUsers.FormattingEnabled = true;
            listBoxUsers.ItemHeight = 20;
            listBoxUsers.Location = new System.Drawing.Point(620, 45);
            listBoxUsers.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            listBoxUsers.Name = "listBoxUsers";
            listBoxUsers.Size = new System.Drawing.Size(200, 324);
            listBoxUsers.TabIndex = 4;
            // 
            // labelUsers
            // 
            labelUsers.AutoSize = true;
            labelUsers.Location = new System.Drawing.Point(620, 20);
            labelUsers.Name = "labelUsers";
            labelUsers.Size = new System.Drawing.Size(120, 20);
            labelUsers.TabIndex = 5;
            labelUsers.Text = "Users:";
            // 
            // labelUserName
            // 
            labelUserName.AutoSize = true;
            labelUserName.Location = new System.Drawing.Point(12, 20);
            labelUserName.Name = "labelUserName";
            labelUserName.Size = new System.Drawing.Size(50, 20);
            labelUserName.TabIndex = 6;
            labelUserName.Text = "You: ...";
            // 
            // Form1
            // 
            AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            ClientSize = new System.Drawing.Size(832, 500);
            Controls.Add(labelUserName);
            Controls.Add(labelUsers);
            Controls.Add(listBoxUsers);
            Controls.Add(textBoxMessage);
            Controls.Add(listBoxMessages);
            Controls.Add(button1);
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            MaximizeBox = false;
            Name = "Form1";
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            Text = "UDP Chat";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private System.Windows.Forms.Button button1;
        private System.Windows.Forms.ListBox listBoxMessages;
        private System.Windows.Forms.TextBox textBoxMessage;
        private System.Windows.Forms.ListBox listBoxUsers;
        private System.Windows.Forms.Label labelUsers;
        private System.Windows.Forms.Label labelUserName;
    }
}

