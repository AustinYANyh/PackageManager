#! usr/bin/python
#coding=utf-8

import sys
import json
import os
import time
import zipfile
from ftplib import FTP
import shutil

#ftp相关配置
ftp_sever = '192.168.0.210'
ftp_user = 'hwclient'
ftp_pwd = 'hw_ftpa206'

ftp_autobuild_dir = './AutoBuild/v1.0.1/src.zip'
zip_autobuild_name = './src.zip'
zip_autobuild_dir = './src'

def release_zip(source_dir, output_filename):
    shutil.unpack_archive(source_dir,output_filename,'zip')
    
def ftp_download_file(ftp_sever,ftp_user,ftp_pwd,ftp_dir,localfile):
    ftp = FTP(ftp_sever)
    ftp.login(ftp_user, ftp_pwd)
    #ftp.retrlines('LIST')
    ftp.cwd('./')
    #ftp.retrlines('LIST')
    f = open(localfile, 'wb')
    ftp_dir = ftp_dir.rstrip('/')
    ftp.retrbinary('RETR %s' % ftp_dir, f.write)
    f.close()

def main():
    print('Start to download!')
    dir=os.path.split(os.path.realpath(__file__))
    os.chdir(dir[0])
    print(os.getcwd())
    ftp_download_file(ftp_sever, ftp_user, ftp_pwd, ftp_autobuild_dir, zip_autobuild_name)
    release_zip(zip_autobuild_name, zip_autobuild_dir)
    os.remove(zip_autobuild_name)

if __name__ == '__main__':
    main()